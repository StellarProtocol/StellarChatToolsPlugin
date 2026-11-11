using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// The Settings window's WHISPERS panel (.Settings.Layout.cs places it): auto-reply (toggle, reply text, cooldown line)
/// and whisper history (keep toggle, stats line, Clear) — or, after Clear, the in-place delete confirm (logic in
/// .WhisperHistory.Settings.cs). Both states occupy the SAME height (<see cref="WhispersBodyH"/>), and the auto-reply
/// reply box keeps its space reserved while auto-reply is off, so the window never changes height (house rule 14).
/// </summary>
public sealed partial class Plugin
{
    // Heights (canvas units) of the normal body: row 24 · reply slot · row 24 · stats line · Clear pill, gaps SetGap.
    // The reply slot = the 28 px reply box + the cooldown line (LineH), pinned by a strut with 2 px of slack in case the
    // box measures a little taller than its 28 px minimum.
    private const float ReplySlotH   = StylePillH + SetGap + LineH + 2f;                              // 56
    private const float WhispersBodyH = StyleChipH + ReplySlotH + StyleChipH + LineH + StylePillH + 4f * SetGap;   // 176
    // Confirm panel = Tile box (PadY 8) of question (2 lines) · consequence (≤4 lines) · Delete/Cancel, gaps SetGap.
    private const float ConfirmPadX = 10f, ConfirmPadY = 8f;
    private const float ConfirmQuestionH = 36f;   // two bold-13 lines
    private const float ConfirmNoteH = WhispersBodyH - 2f * ConfirmPadY - ConfirmQuestionH - StylePillH - 2f * SetGap;   // 84
    private const float ConfirmInnerW = SetInnerW - 2f * ConfirmPadX;
    private const float ConfirmCellW  = (ConfirmInnerW - SetGap) / 2f;   // each of Delete / Cancel
    private const float ClearLabelW   = SetInnerW - 2f * StylePillPadX;   // the full-width Clear pill's label box

    private HudElement BuildWhispersPanel()
    {
        const string Section = "chat.settings.section.whispers";
        return SectionPanel(() => _loc.T(Section), null,
            // One Row pinned by a WhispersBodyH strut holding the two states (a Row centres its children, so a state that
            // measured a pixel short would sit centred instead of moving the panel).
            new RowElement(new HudElement[]
            {
                new SpacerElement(1f, WhispersBodyH),
                new CellElement(new ConditionalElement(() => _histConfirming,
                    BuildHistoryConfirm(), BuildWhispersBody(Section)), Weight: 1f),
            }, Gap: 0.01f));
    }

    private HudElement BuildWhispersBody(string section) => new ColumnElement(new HudElement[]
    {
        SettingRow(() => _loc.T("chat.autoReply.label"), OnOffChip(() => _autoReplyEnabled, SetAutoReply),
            new HelpTip("chat.autoReply.label", section, () => _loc.T("chat.settings.help.autoReply"))),
        // Reply box + cooldown while ON; while OFF the same strut keeps the space (no Else branch → empty slot).
        new RowElement(new HudElement[]
        {
            new SpacerElement(1f, ReplySlotH),
            new CellElement(new ConditionalElement(() => _autoReplyEnabled, new ColumnElement(new HudElement[]
            {
                // SingleLine: a long reply scrolls sideways in the 28 px box instead of wrapping and growing the window.
                // Enter (or leaving the field) saves; typing saves ~1 s after the last key and on close (.Config.cs).
                new InputElement(() => _autoReplyText, s => { _autoReplyText = s; SaveAutoReplyText(); },
                    SetInnerW - 2f, OnChange: OnAutoReplyTyped) { SingleLine = true },
                FixedLine(CooldownText, () => MutedColor()),
            }, Gap: SetGap)), Weight: 1f),
        }, Gap: 0.01f),
        SettingRow(() => _loc.T("chat.history.keep"), OnOffChip(() => _keepWhisperHistory, SetKeepWhisperHistory),
            new HelpTip("chat.history.keep", section, () => _loc.T("chat.history.note"))),
        // Stats ("<character> · N conversations · M messages"), or the green "Cleared" right after a delete — just above
        // the Clear pill, where the click happened (house rule 13).
        FixedLine(HistStatsText, () => HistJustCleared() ? _clearedText.Get(Menu.Success, PanelColor(), PillMinContrast)
                                                         : MutedColor()),
        DangerButton(() => _loc.T("chat.history.clear"), OpenClearHistoryConfirm, ClearLabelW, PanelColor,
            enabled: HistHasSaved),
    }, Gap: SetGap);

    private readonly ContrastCache _clearedText = new();

    // The house destructive confirm, in place: Tile box → bold question, muted consequence, [Delete | Cancel].
    private HudElement BuildHistoryConfirm() => new BubbleElement(new ColumnElement(new HudElement[]
    {
        new TextElement(() => _loc.T("chat.history.confirmQuestion"), Color: () => TextColor(), FontSize: 13)
            { Bold = true, MaxLines = 2, Height = ConfirmQuestionH },
        new TextElement(() => _loc.T("chat.history.confirmNote"), Color: () => MutedColor())
            { MaxLines = 4, Height = ConfirmNoteH },
        PillPair(
            DangerButton(() => _loc.T("chat.history.delete"), ConfirmClearHistory, ConfirmCellW - 2f * StylePillPadX, TileColor),
            NeutralButton(() => _loc.T("chat.history.cancel"), CancelClearHistory, ConfirmCellW - 2f * StylePillPadX, TileColor),
            ConfirmCellW),
    }, Gap: SetGap))
    {
        FillColorFn = () => Fade(TileColor()), CornerRadius = TileRadius, PaddingX = ConfirmPadX, PaddingY = ConfirmPadY,
    };

    // "Cooldown: 30s per sender" — formatted once per language (the Func is polled every refresh).
    private string? _cdLang;
    private string _cdText = string.Empty;

    private string CooldownText()
    {
        var lang = _loc.Language;
        if (!string.Equals(lang, _cdLang, StringComparison.Ordinal))
        {
            _cdLang = lang;
            _cdText = _loc.TFormat("chat.cooldown", AutoReplyCooldown.TotalSeconds);
        }
        return _cdText;
    }
}
