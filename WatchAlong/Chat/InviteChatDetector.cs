using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Dalamud.Game.Chat;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using WatchAlong.Shared.Invites;

namespace WatchAlong.Chat;

/// <summary>
/// Hooks <see cref="IChatGui.ChatMessage"/> and, for any chat line containing a well-formed
/// <c>WA1:</c> invite or <c>WA1P:</c> position-share token, replaces that raw token in the local
/// display with a clickable link — group-invites spec "Invites appearing in chat are offered as a
/// confirmable join action" and "Position-share messages are offered as a confirmable sync
/// action". This is a local rendering change only: the raw token is what was actually
/// sent/received and stays that way for every other client; never joins or syncs on its own — a
/// decode/validation failure leaves the message completely untouched (no link, no stripping, no
/// chat error), per both "Malformed or invalid ... is ignored" scenarios.
/// </summary>
public sealed partial class InviteChatDetector : IDisposable
{
    [GeneratedRegex(@"WA1P:[A-Za-z0-9_-]+")]
    private static partial Regex PositionShareTokenPattern();

    [GeneratedRegex(@"WA1:[A-Za-z0-9_-]+")]
    private static partial Regex InviteTokenPattern();

    private readonly IChatGui _chatGui;
    private readonly Action<DecodedInvite, string?> _onJoinLinkClicked;
    private readonly Action<DecodedPositionShare> _onSyncLinkClicked;
    private uint _nextLinkCommandId;

    /// <param name="onJoinLinkClicked">
    /// Invoked with the decoded invite and, when known, the chat-line sender's character name —
    /// the tell-roster target once the invite is accepted (roster spec "A joined tell is sent
    /// back to whoever's invite was accepted"). Null for a message this client can't attribute to
    /// a sender.
    /// </param>
    public InviteChatDetector(IChatGui chatGui, Action<DecodedInvite, string?> onJoinLinkClicked, Action<DecodedPositionShare> onSyncLinkClicked)
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
        var senderName = message.Sender.TextValue;
        var shareMatches = PositionShareTokenPattern().Matches(text);
        var inviteMatches = InviteTokenPattern().Matches(text);
        if (shareMatches.Count == 0 && inviteMatches.Count == 0)
            return;

        var decoded = new List<ChatTokenRewriter.DecodedMatch>();
        var onClicks = new List<Action>();

        // Position shares first: WA1P: never matches the WA1: pattern (its next character is
        // 'P', not ':'), so the two are already unambiguous — checked separately regardless, to
        // keep "can open a room" and "structurally cannot" easy to reason about (design.md).
        foreach (Match match in shareMatches)
        {
            if (!InviteCodec.TryDecodePositionShare(match.Value, out var share))
                continue;
            decoded.Add(new ChatTokenRewriter.DecodedMatch(match, $"Sync screen to {share.Name}'s placement"));
            onClicks.Add(() => _onSyncLinkClicked(share));
        }

        foreach (Match match in inviteMatches)
        {
            if (!InviteCodec.TryDecodeInvite(match.Value, out var invite))
                continue;
            decoded.Add(new ChatTokenRewriter.DecodedMatch(match, $"Join watch-along: {invite.Name}"));
            var attributedSender = string.IsNullOrWhiteSpace(senderName) ? null : senderName;
            onClicks.Add(() => _onJoinLinkClicked(invite, attributedSender));
        }

        // group-invites spec "Malformed or invalid invite is ignored": a token-shaped match that
        // fails to decode must leave the message completely untouched — reassigning here
        // regardless would still flip IMutableChatMessage.MessageModified.
        if (decoded.Count == 0)
            return;

        // Rebuilt from the flattened text rather than the original SeString (design.md "Rebuild
        // the displayed message from TextValue, not from the payload list"): any other payload
        // sharing this line (an item link, say) is lost, an accepted trade-off since these lines
        // are, in practice, just the pasted token plus optional plain-text commentary.
        var (strippedText, labels) = ChatTokenRewriter.Strip(text, decoded);
        var builder = new SeStringBuilder().AddText(strippedText);
        for (var i = 0; i < labels.Count; i++)
            AppendLink(builder, labels[i], onClicks[i]);

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
