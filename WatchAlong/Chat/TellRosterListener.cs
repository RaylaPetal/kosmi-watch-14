using System;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Plugin.Services;
using WatchAlong.Shared.Invites;

namespace WatchAlong.Chat;

/// <summary>
/// Listens for an incoming `/tell` matching <see cref="RosterAnnouncement"/>'s
/// "[WatchAlong] &lt;name&gt; joined" shape and reports the announced name — the receiving half
/// of the tell-relay session roster. Kosmi's own room chat can't carry this (WatchAlong joins
/// Kosmi anonymously, so Kosmi's member list never has a real display name), so an accepted
/// invite instead tells the inviter directly; this is what listens for that tell. Read-only: it
/// never modifies or hides the tell in the player's own chat log.
/// </summary>
public sealed class TellRosterListener : IDisposable
{
    private readonly IChatGui _chatGui;
    private readonly Action<string> _onMemberAnnounced;

    public TellRosterListener(IChatGui chatGui, Action<string> onMemberAnnounced)
    {
        _chatGui = chatGui;
        _onMemberAnnounced = onMemberAnnounced;
        _chatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose() => _chatGui.ChatMessage -= OnChatMessage;

    private void OnChatMessage(IHandleableChatMessage message)
    {
        if (message.LogKind != XivChatType.TellIncoming)
            return;

        if (RosterAnnouncement.TryParse(message.Message.TextValue, out var name))
            _onMemberAnnounced(name);
    }
}
