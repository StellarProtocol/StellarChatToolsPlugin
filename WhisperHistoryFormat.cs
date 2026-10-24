using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Stellar.Abstractions.Domain;

namespace Stellar.ChatTools;

/// <summary>One saved whisper (a line of a peer's <c>.jsonl</c> file). Plain data: written on the main thread when
/// recorded, then only read by the I/O worker while serializing (the one later main-thread write is the MsgId upgrade in
/// <see cref="WhisperHistoryFormat.SameMessage"/>'s caller — a single aligned long, harmless to race).</summary>
internal sealed class SavedWhisper
{
    public long   MsgId;        // server id; 0 = unknown (some echoes carry none)
    public long   TsMs;         // Unix ms, UTC (the wire timestamp — seconds — ×1000)
    public long   SenderId;     // charId; our own lines carry OUR charId even when the echo had SenderId 0
    public long   PeerId;       // the other party (= the file's peer)
    public bool   Self;         // we sent it
    public string SenderName = "";
    public string Text = "";    // raw, <sprite=N> tags included
    public int    StickerId;
    public int    Type;         // (int)ChatMessageType
    public ChatMessage? View;   // main thread only: the display record, built once (stable MessageKey / row id)

    public DateTime Utc => DateTimeOffset.FromUnixTimeMilliseconds(TsMs).UtcDateTime;
}

/// <summary>One <c>peers.json</c> entry: enough to list a saved conversation without opening its file.</summary>
internal sealed class SavedPeerInfo
{
    public long   CharId;
    public string Name = "";
    public int    Level;
    public long   LastMs;       // newest saved message (Unix ms) — sorts the list after a maintenance wipe
    public string Preview = ""; // newest message's text (raw) …
    public int    Kind;         // … or its kind: 0 text, 3 sticker (the conversation preview's ChitChatMsgType)
    public int    Count;        // messages in the peer file (Settings stats; refreshed whenever the file is loaded)

    public SavedPeerInfo Clone() => (SavedPeerInfo)MemberwiseClone();
}

/// <summary>
/// The whisper-history file format. Per character: <c>whisper.&lt;uid&gt;/peers.json</c> (the index) and one
/// <c>whisper.&lt;uid&gt;/&lt;peerCharId&gt;.jsonl</c> per conversation — one JSON object per line, oldest first:
/// <c>{"id":…,"ts":…,"from":…,"peer":…,"self":…,"name":"…","text":"…","sticker":…,"type":…}</c>.
/// Readers skip any line that doesn't parse (a torn or hand-edited line costs that line only). Files are always
/// rewritten whole through <c>IPluginDataStore.Write</c>, which writes a temp file and renames it over the old one.
/// </summary>
internal static class WhisperHistoryFormat
{
    public const int KeepPerPeer = 500;   // newest kept per conversation …
    public const int CompactAt   = 600;   // … trimmed back to KeepPerPeer once a file grows past this
    public const long DupWindowMs = 2000; // content-match tolerance between two copies of one message

    // Same message? An equal server id decides it; otherwise (either id unknown) the content within ±2 s.
    public static bool SameMessage(SavedWhisper a, SavedWhisper b)
    {
        if (a.MsgId != 0 && b.MsgId != 0) return a.MsgId == b.MsgId;
        return SameContent(a.TsMs, a.SenderId, a.StickerId, a.Text, b.TsMs, b.SenderId, b.StickerId, b.Text);
    }

    public static bool SameContent(long tsA, long fromA, int stA, string textA, long tsB, long fromB, int stB, string textB)
        => Math.Abs(tsA - tsB) <= DupWindowMs && fromA == fromB && stA == stB && string.Equals(textA, textB, StringComparison.Ordinal);

    // Insert w into list (kept sorted by TsMs, oldest first) unless it is already there. Returns false for a duplicate
    // (an existing copy without a server id adopts w's). Usually an append: live messages arrive newest-last.
    public static bool AddSorted(List<SavedWhisper> list, SavedWhisper w)
    {
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var e = list[i];
            if (e.TsMs < w.TsMs - DupWindowMs) break;   // sorted → nothing older can match (ids are only checked in-window too)
            if (SameMessage(e, w)) { if (e.MsgId == 0 && w.MsgId != 0) e.MsgId = w.MsgId; return false; }
        }
        // A server-id duplicate can sit outside the content window (history replays carry the original time, so this
        // only guards a clock skew) — check the ids over the whole list when w has one.
        if (w.MsgId != 0)
            for (int i = 0; i < list.Count; i++) if (list[i].MsgId == w.MsgId) return false;
        int at = list.Count;
        while (at > 0 && list[at - 1].TsMs > w.TsMs) at--;
        list.Insert(at, w);
        return true;
    }

    // Trim to the newest KeepPerPeer once past CompactAt. Returns true when it removed anything.
    public static bool Compact(List<SavedWhisper> list)
    {
        if (list.Count <= CompactAt) return false;
        list.RemoveRange(0, list.Count - KeepPerPeer);
        return true;
    }

    // ── Peer file (.jsonl) ──────────────────────────────────────────────────────────────────────────────────────────
    public static byte[] WriteLines(IReadOnlyList<SavedWhisper> msgs)
    {
        using var ms = new MemoryStream(msgs.Count * 160);
        var opts = new JsonWriterOptions { Indented = false };
        foreach (var m in msgs)
        {
            using (var w = new Utf8JsonWriter(ms, opts))
            {
                w.WriteStartObject();
                w.WriteNumber("id", m.MsgId);
                w.WriteNumber("ts", m.TsMs);
                w.WriteNumber("from", m.SenderId);
                w.WriteNumber("peer", m.PeerId);
                w.WriteBoolean("self", m.Self);
                w.WriteString("name", m.SenderName);
                w.WriteString("text", m.Text);
                w.WriteNumber("sticker", m.StickerId);
                w.WriteNumber("type", m.Type);
                w.WriteEndObject();
            }
            ms.WriteByte((byte)'\n');
        }
        return ms.ToArray();
    }

    public static List<SavedWhisper> ReadLines(byte[]? bytes, long peer)
    {
        var list = new List<SavedWhisper>();
        if (bytes == null || bytes.Length == 0) return list;
        int start = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n') continue;
            int len = i - start;
            if (len > 1)
            {
                try
                {
                    var r = new Utf8JsonReader(new ReadOnlySpan<byte>(bytes, start, len));
                    using var doc = JsonDocument.ParseValue(ref r);
                    var e = doc.RootElement;
                    var m = new SavedWhisper
                    {
                        MsgId = Long(e, "id"), TsMs = Long(e, "ts"), SenderId = Long(e, "from"), PeerId = Long(e, "peer"),
                        Self = e.TryGetProperty("self", out var s) && s.ValueKind == JsonValueKind.True,
                        SenderName = Str(e, "name"), Text = Str(e, "text"),
                        StickerId = (int)Long(e, "sticker"), Type = (int)Long(e, "type"),
                    };
                    if (m.PeerId == 0) m.PeerId = peer;
                    if (m.TsMs > 0 && m.PeerId == peer) list.Add(m);   // a line from another file / without a time is junk
                }
                catch { /* bad line → skipped */ }
            }
            start = i + 1;
        }
        list.Sort((a, b) => a.TsMs.CompareTo(b.TsMs));   // List.Sort isn't stable, but equal-ms lines are near-impossible
        return list;
    }

    // ── Index (peers.json) ──────────────────────────────────────────────────────────────────────────────────────────
    public static byte[] WriteIndex(string charName, IReadOnlyList<SavedPeerInfo> peers)
    {
        using var ms = new MemoryStream(256 + peers.Count * 120);
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 1);
            w.WriteString("char", charName);
            w.WriteStartArray("peers");
            foreach (var p in peers)
            {
                w.WriteStartObject();
                w.WriteNumber("id", p.CharId);
                w.WriteString("name", p.Name);
                w.WriteNumber("lv", p.Level);
                w.WriteNumber("last", p.LastMs);
                w.WriteString("preview", p.Preview);
                w.WriteNumber("kind", p.Kind);
                w.WriteNumber("count", p.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    public static List<SavedPeerInfo> ReadIndex(byte[]? bytes)
    {
        var list = new List<SavedPeerInfo>();
        if (bytes == null || bytes.Length == 0) return list;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("peers", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;
            foreach (var e in arr.EnumerateArray())
            {
                try
                {
                    var p = new SavedPeerInfo
                    {
                        CharId = Long(e, "id"), Name = Str(e, "name"), Level = (int)Long(e, "lv"), LastMs = Long(e, "last"),
                        Preview = Str(e, "preview"), Kind = (int)Long(e, "kind"), Count = (int)Long(e, "count"),
                    };
                    if (p.CharId != 0) list.Add(p);
                }
                catch { /* bad entry → skipped */ }
            }
        }
        catch { /* unreadable index → empty; the peer files are still read when their conversation opens */ }
        return list;
    }

    private static long Long(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;

    private static string Str(JsonElement e, string key)
        => e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
