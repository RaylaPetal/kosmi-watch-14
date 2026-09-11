using System;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using WatchAlong.Shared.Invites;

namespace WatchAlong.Chat;

/// <summary>
/// Hooks <see cref="IChatGui.ChatMessage"/> and appends a clickable link next to any chat line
/// containing a well-formed <c>WA1:</c> invite or <c>WA1P:</c> position-share token — group-invites
/// spec "Invites appearing in chat are offered as a confirmable join action" and "Position-share
/// messages are offered as a confirmable sync action". Never edits the original message text
/// beyond appending the link, never joins or syncs on its own — a decode/validation failure is
/// silently ignored (no link, no chat error), per both "Malformed or invalid ... is ignored"
/// scenarios.
/// </summary>
public sealed partial class InviteChatDetector : IDisposable
{
    [GeneratedRegex(@"WA1P:[A-Za-z0-9_-]+")]
    private static partial Regex PositionShareTokenPattern();

    [GeneratedRegex(@"WA1:[A-Za-z0-9_-]+")]
    private static partial Regex InviteTokenPattern();

    private readonly IChatGui _chatGui;
    private readonly Action<DecodedInvite> _onJoinLinkClicked;
    private readonly Action<DecodedPositionShare> _onSyncLinkClicked;
    private uint _nextLinkCommandId;

    public InviteChatDetector(IChatGui chatGui, Action<DecodedInvite> onJoinLinkClicked, Action<DecodedPositionShare> onSyncLinkClicked)
    {
        _chatGui = chatGui;
        _onJoinLinkClicked = onJoinLinkClicked;
        _onSyncLinkClicked = onSyncLinkClicked;
        _chatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        _chatGui.ChatMessage -= OnChatMessage;
        _chatGui.RemoveChatLinkHandler();
    }

    private void OnChatMessage(IHandleableChatMessage message)
    {
        var text = message.Message.TextValue;
        var shareMatches = PositionShareTokenPattern().Matches(text);
        var inviteMatches = InviteTokenPattern().Matches(text);
        if (shareMatches.Count == 0 && inviteMatches.Count == 0)
            return;

        var builder = new SeStringBuilder().Append(message.Message);
        var linkAdded = false;

        // Position shares first: WA1P: never matches the WA1: pattern (its next character is
        // 'P', not ':'), so the two are already unambiguous — checked separately regardless, to
        // keep "can open a room" and "structurally cannot" easy to reason about (design.md).
        foreach (Match match in shareMatches)
        {
            if (!InviteCodec.TryDecodePositionShare(match.Value, out var share))
                continue;
            AppendLink(builder, $"Sync screen to {share.Name}'s placement", () => _onSyncLinkClicked(share));
            linkAdded = true;
        }

        foreach (Match match in inviteMatches)
        {
            if (!InviteCodec.TryDecodeInvite(match.Value, out var invite))
                continue;
            AppendLink(builder, $"Join watch-along: {invite.Name}", () => _onJoinLinkClicked(invite));
            linkAdded = true;
        }

        // group-invites spec "Malformed or invalid invite is ignored": a token-shaped match that
        // fails to decode must leave the message untouched, not just link-less — reassigning here
        // regardless would still flip IMutableChatMessage.MessageModified.
        if (linkAdded)
            message.Message = builder.Build();
    }

    // ponytail: one link handler registered per detected token, never pruned — RegisteredLinkHandlers
    // grows for the life of the session. Add expiry/pruning if a long play session with heavy
    // invite traffic makes that measurably matter.
    private void AppendLink(SeStringBuilder builder, string label, Action onClick)
    {
        var linkStart = _chatGui.AddChatLinkHandler(_nextLinkCommandId++, (_, _) => onClick());
        builder.Append(" ").Add(linkStart).AddUiForeground(500).AddText($"[{label}]").AddUiForegroundOff().Add(RawPayload.LinkTerminator);
    }
}
