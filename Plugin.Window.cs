using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.ChatTools;

/// <summary>
/// Window body layout. Body is a ColumnElement: [tab bar] [log scroll that fills] [new-below pill] [composer]. Only the
/// scroll carries flexibleHeight (via ScrollElement), so it alone absorbs the resize slack; the rest stay content-sized.
/// Settings live in their own window (.Settings.cs), so this layout never changes when they open.
/// </summary>
public sealed partial class Plugin
{
    // NOTE: the log font-size control was removed — TextElement font size is ignored on the GlassMenu surface
    // (only HudOverlay honours it), so an A-/A+ stepper here was a confirmed no-op. Honouring a per-log font
    // size needs a framework change (per-TextElement size on the Menu text path); the old config key is simply
    // left dormant (no longer read or written).

    private HudElement BuildRoot() => new ColumnElement(new HudElement[]
    {
        BuildTabBar(),       // .Tabs.cs
        BuildLogArea(),      // .WhisperList.cs — the log (.Log.cs, the only flexible-height section; grows with the
                             // window), with the conversation list + header beside/above it while Whisper is open
        BuildNewBelowBar(),  // .NewBelow.cs — "↓ N new messages" pill, shown only while scrolled up with unseen rows
        BuildComposer(),     // .Composer.cs
    }, Gap: 6f);
}
