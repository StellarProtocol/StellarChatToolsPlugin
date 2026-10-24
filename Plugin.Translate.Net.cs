using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Stellar.ChatTools;

/// <summary>
/// The translate request pipeline behind the per-message 文A button (.Translate.cs). Google Translate's keyless
/// endpoint as documented in KB Google-Translate-Private-Endpoint.md — the Android-app client id is the one Google's
/// anti-abuse gate tolerates (<c>gtx</c> / <c>translate.googleapis.com</c> get throttled fast):
/// <code>POST https://translate.google.com/translate_a/single?client=at&amp;dt=t&amp;dt=rm&amp;dj=1
/// Content-Type: application/x-www-form-urlencoded; charset=utf-8
/// sl=auto&amp;tl=&lt;target&gt;&amp;q=&lt;text&gt;</code>
/// Same split as <c>@vitalets/google-translate-api</c> v9: the fixed flags in the query string, the per-call values in
/// the form body (so chat text never appears in a URL or a log). <c>dj=1</c> returns a JSON OBJECT:
/// <c>{"sentences":[{"trans":"…","orig":"…"},…,{"translit":"…"}],"src":"th",…}</c> — the translation is every
/// sentence's <c>trans</c> concatenated (the <c>dt=rm</c> transliteration entry has none), <c>src</c> the detected source.
///
/// Threading: all state here is main-thread-only except the request itself, which runs on the thread pool; its result
/// hops back through <c>IFramework.Post</c>. At most <see cref="TrMaxInFlight"/> requests run at once, the rest wait in a
/// FIFO queue. An unofficial endpoint — failures (network, 8 s timeout, HTTP error, unexpected JSON) just mark the row
/// failed (click retries) and are logged at most once a minute, never with message text.
/// </summary>
public sealed partial class Plugin
{
    private const string TrEndpoint = "https://translate.google.com/translate_a/single?client=at&dt=t&dt=rm&dj=1";
    private const int TrMaxInFlight = 2;
    private const float TrFailLogIntervalSec = 60f;
    private static readonly HttpClient TrHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    // Outgoing = a composer draft (.TranslateOut.cs), not a log message: its result goes to the composer, not a row.
    private readonly record struct TrJob(long Key, string Text, string Target, bool Outgoing = false);
    private sealed record TrResult(string Text, string Src, bool Same);

    private readonly Queue<TrJob> _trQueue = new();
    private int _trInFlight;
    private readonly CancellationTokenSource _trCts = new();
    private bool _trDisposed;
    private float _trLastFailLog = -1f;
    private int _trSuppressedFails;

    // Session caches: per (message, target) and per (exact text, target) — the same line posted twice (or the same
    // message after a ring shift) never re-requests. Bounded: cleared wholesale when they grow past TrCacheCap.
    private const int TrCacheCap = 2000;
    private readonly Dictionary<(long, string), TrResult> _trByMsg = new();
    private readonly Dictionary<(string, string), TrResult> _trByText = new();

    private bool TryCachedTranslation(long key, string text, string target, out TrResult result)
    {
        if (_trByMsg.TryGetValue((key, target), out result!)) return true;
        if (_trByText.TryGetValue((text, target), out result!)) { _trByMsg[(key, target)] = result; return true; }
        return false;
    }

    private void CacheTranslation(long key, string text, string target, TrResult r)
    {
        if (_trByMsg.Count >= TrCacheCap) _trByMsg.Clear();
        if (_trByText.Count >= TrCacheCap) _trByText.Clear();
        _trByMsg[(key, target)] = r;
        _trByText[(text, target)] = r;
    }

    private void EnqueueTranslation(long key, string text, string target) => EnqueueTranslation(new TrJob(key, text, target));

    private void EnqueueTranslation(TrJob job)
    {
        _trQueue.Enqueue(job);
        PumpTranslations();
    }

    // Start queued jobs while under the in-flight cap. A job whose text got cached meanwhile (a duplicate line that a
    // sibling request just answered) completes at once without a request.
    private void PumpTranslations()
    {
        while (!_trDisposed && _trInFlight < TrMaxInFlight && _trQueue.Count > 0)
        {
            var job = _trQueue.Dequeue();
            if (TryCachedTranslation(job.Key, job.Text, job.Target, out var hit)) { ApplyTranslation(job, hit); continue; }
            _trInFlight++;
            StartTranslationRequest(job);
        }
    }

    private void StartTranslationRequest(TrJob job)
    {
        var protectedText = ProtectTags(job.Text, out var tags);
        var ct = _trCts.Token;
        _ = Task.Run(async () =>
        {
            string? text = null, src = null, error = null;
            try
            {
                var form = "sl=auto&tl=" + Uri.EscapeDataString(job.Target) + "&q=" + Uri.EscapeDataString(protectedText);
                using var body = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded");
                using var resp = await TrHttp.PostAsync(TrEndpoint, body, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) error = "HTTP " + (int)resp.StatusCode;
                else
                {
                    var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!TryParseTranslation(json, out text, out src)) error = "unexpected response";
                }
            }
            catch (TaskCanceledException) { error = ct.IsCancellationRequested ? "cancelled" : "timeout"; }
            catch (HttpRequestException ex) { error = "network: " + ex.Message; }
            catch (Exception ex) { error = ex.GetType().Name; }
            var restored = text != null ? RestoreTags(text, tags) : null;
            _services.Framework.Post(() => OnTranslationDone(job, restored, src, error));
        });
    }

    // Main thread. Fills the caches, updates the row (if it still waits for THIS target), then starts the next job.
    private void OnTranslationDone(TrJob job, string? text, string? src, string? error)
    {
        if (_trDisposed) return;
        _trInFlight = Math.Max(0, _trInFlight - 1);
        if (error == null && text != null)
        {
            var source = ToGoogleCode(src);
            var r = new TrResult(text, source, source.Length > 0 && string.Equals(source, job.Target, StringComparison.Ordinal));
            CacheTranslation(job.Key, job.Text, job.Target, r);
            ApplyTranslation(job, r);
        }
        else
        {
            FailTranslation(job);
            LogTranslateFailure(error ?? "empty translation");
        }
        PumpTranslations();
    }

    // At most one warning a minute; the count of swallowed ones rides on the next. Never logs message text.
    private void LogTranslateFailure(string error)
    {
        var now = _services.Framework.TimeNow;
        if (_trLastFailLog >= 0f && now - _trLastFailLog < TrFailLogIntervalSec) { _trSuppressedFails++; return; }
        var more = _trSuppressedFails > 0 ? $" (+{_trSuppressedFails} more since the last report)" : "";
        _services.Log.Warning($"[ChatTools][translate] request failed: {error}{more}");
        _trLastFailLog = now;
        _trSuppressedFails = 0;
    }

    private static bool TryParseTranslation(string json, out string? text, out string? src)
    {
        text = null; src = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sentences", out var sentences)
                || sentences.ValueKind != JsonValueKind.Array) return false;
            var sb = new StringBuilder();
            foreach (var s in sentences.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("trans", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            if (root.TryGetProperty("src", out var sv) && sv.ValueKind == JsonValueKind.String) src = sv.GetString();
            text = sb.ToString().Trim();
            return text.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    // ── Token protection ──────────────────────────────────────────────────────────────────────────────────────────
    // Inbound chat keeps the game's <sprite=N> emoji tags (and could carry other TMP tags). Google would translate or
    // mangle them, so each tag is swapped for a numbered ⟦k⟧ placeholder it leaves alone and swapped back afterwards —
    // the emoji then still render through the RichEmoji TMP body. Google sometimes pads a placeholder with spaces
    // ("⟦ 0 ⟧"), so the restore is whitespace-tolerant; a placeholder it dropped entirely gets its tag appended at the
    // end, so no emoji is ever lost.
    private static readonly Regex TrTagRx = new(@"<[^<>\r\n]{1,64}>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TrPlaceholderRx = new(@"⟦\s*(\d{1,3})\s*⟧", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string ProtectTags(string text, out string[] tags)
    {
        var found = new List<string>();
        var result = TrTagRx.Replace(text, m => { found.Add(m.Value); return "⟦" + (found.Count - 1) + "⟧"; });
        tags = found.ToArray();
        return result;
    }

    private static string RestoreTags(string translated, string[] tags)
    {
        if (tags.Length == 0) return translated;
        var used = new bool[tags.Length];
        var result = TrPlaceholderRx.Replace(translated, m =>
        {
            if (int.TryParse(m.Groups[1].Value, out var k) && k >= 0 && k < tags.Length) { used[k] = true; return tags[k]; }
            return "";   // a placeholder number we never issued — drop it rather than show it
        });
        var sb = new StringBuilder(result);
        for (int k = 0; k < tags.Length; k++) if (!used[k]) sb.Append(' ').Append(tags[k]);
        return sb.ToString();
    }

    private void DisposeTranslate()
    {
        _trDisposed = true;
        _trQueue.Clear();
        try { _trCts.Cancel(); } catch { /* swallow */ }
        _trCts.Dispose();
    }
}
