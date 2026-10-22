using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Djama.Api.Hubs;

/// <summary>
/// Real-time channel: /hubs/chat?access_token=JWT   (full list of events: docs/api-contracts.md)
/// Client -> server : SendMessage(conversationId, text, replyToId?) · Typing · MarkRead
///                    CallInvite(conversationId, media) · CallAccept · CallReject · CallEnd · CallSignal(callId, kind, payload)
/// Server -> client : ReceiveMessage, MessageEdited, MessagesRead, MessageDeleted, ReactionChanged, Typing, PresenceChanged,
///                    ConversationChanged, NotificationReceived, IncomingCall, CallAccepted, CallEnded, CallSignal
/// </summary>
[Authorize]
public class ChatHub(ChatService chat, CallService calls, PresenceTracker presence) : Hub
{
    private Guid Me => Guid.Parse(Context.UserIdentifier!);

    public override async Task OnConnectedAsync()
    {
        if (presence.Connected(Me)) await chat.BroadcastPresenceAsync(Me, true);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (presence.Disconnected(Me))
        {
            await calls.EndAllForUserAsync(Me);          // closing the tab hangs up
            await chat.SetLastSeenAsync(Me);
            await chat.BroadcastPresenceAsync(Me, false);
        }
        await base.OnDisconnectedAsync(exception);
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (DomainException ex) { throw new HubException($"{ex.Code}: {ex.Message}"); }
    }

    private static Task Guard(Func<Task> action) => Guard(async () => { await action(); return 0; });

    public Task<MessageDto> SendMessage(Guid conversationId, string text, Guid? replyToId) =>
        Guard(() => chat.SendMessageAsync(conversationId, Me, text, replyToId));

    public Task Typing(Guid conversationId) => Guard(() => chat.NotifyTypingAsync(conversationId, Me));
    public Task MarkRead(Guid conversationId) => Guard(() => chat.MarkReadAsync(conversationId, Me));

    // ---- calls (1-to-1) ----
    public Task<Guid> CallInvite(Guid conversationId, string media) => Guard(() => calls.InviteAsync(Me, conversationId, media));
    public Task CallAccept(Guid callId) => Guard(() => calls.AcceptAsync(callId, Me));
    public Task CallReject(Guid callId) => Guard(() => calls.RejectAsync(callId, Me));
    public Task CallEnd(Guid callId) => Guard(() => calls.EndAsync(callId, Me));
    public Task CallSignal(Guid callId, string kind, string payload) => Guard(() => calls.SignalAsync(callId, Me, kind, payload));
}
