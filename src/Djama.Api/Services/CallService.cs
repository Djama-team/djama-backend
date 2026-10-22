using Djama.Api.Data;
using Djama.Api.Domain;
using Djama.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Services;

public class CallSession
{
    public Guid CallId { get; init; } = Guid.NewGuid();
    public Guid ConversationId { get; init; }
    public Guid CallerId { get; init; }
    public Guid CalleeId { get; init; }
    public string Media { get; init; } = "Audio";          // "Audio" | "Video"
    public DateTime? AcceptedAt { get; set; }
    public Guid Other(Guid me) => me == CallerId ? CalleeId : CallerId;
    public bool Has(Guid userId) => userId == CallerId || userId == CalleeId;
}

/// <summary>Calls in progress (kept in memory: a call never outlives the server).</summary>
public class CallTracker
{
    private readonly Dictionary<Guid, CallSession> _sessions = new();
    private readonly object _lock = new();

    public CallSession Start(Guid convId, Guid callerId, Guid calleeId, string media)
    {
        var s = new CallSession { ConversationId = convId, CallerId = callerId, CalleeId = calleeId, Media = media };
        lock (_lock) _sessions[s.CallId] = s;
        return s;
    }
    public CallSession? Get(Guid id) { lock (_lock) return _sessions.GetValueOrDefault(id); }
    public bool IsBusy(Guid userId) { lock (_lock) return _sessions.Values.Any(s => s.Has(userId)); }
    public CallSession? Remove(Guid id) { lock (_lock) return _sessions.Remove(id, out var s) ? s : null; }
    public List<CallSession> RemoveAllFor(Guid userId)
    {
        lock (_lock)
        {
            var mine = _sessions.Values.Where(s => s.Has(userId)).ToList();
            foreach (var s in mine) _sessions.Remove(s.CallId);
            return mine;
        }
    }
}

/// <summary>
/// 1-to-1 audio/video calls. The server only does SIGNALING (who calls whom, offer/answer/ICE relay);
/// the audio/video itself flows peer-to-peer between browsers (WebRTC).
/// </summary>
public class CallService(DjamaDbContext db, ChatService chat, IRealtimeNotifier rt, CallTracker tracker, PresenceTracker presence)
{
    public async Task<Guid> InviteAsync(Guid callerId, Guid convId, string media)
    {
        if (media != "Audio" && media != "Video") throw DomainException.Validation("Media must be Audio or Video");
        var conv = await db.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == convId)
                   ?? throw DomainException.NotFound("Conversation not found");
        if (conv.Type != ConversationType.Direct) throw DomainException.Validation("Calls are available in private chats only");
        if (conv.Members.All(m => m.UserId != callerId)) throw DomainException.Forbidden("Not a member of this conversation");
        var calleeId = conv.Members.First(m => m.UserId != callerId).UserId;
        if (await chat.IsBlockedBetweenAsync(callerId, calleeId)) throw DomainException.Blocked();
        if (tracker.IsBusy(callerId) || tracker.IsBusy(calleeId)) throw new DomainException(409, "BUSY", "User is already in a call");
        if (!presence.IsOnline(calleeId))
        {
            await chat.AddCallMessageAsync(convId, callerId, media, "Missed", 0);
            throw new DomainException(409, "OFFLINE", "User is offline");
        }

        var session = tracker.Start(convId, callerId, calleeId, media);
        var caller = await db.Users.FindAsync(callerId) ?? throw DomainException.NotFound("User not found");
        await rt.SendToUsersAsync(new[] { calleeId }, "IncomingCall", new IncomingCallEvent(session.CallId, convId, chat.ToUserDto(caller), media));
        return session.CallId;
    }

    public async Task AcceptAsync(Guid callId, Guid userId)
    {
        var s = tracker.Get(callId) ?? throw DomainException.NotFound("Call not found");
        if (s.CalleeId != userId) throw DomainException.Forbidden("Only the callee can accept");
        s.AcceptedAt = DateTime.UtcNow;
        await rt.SendToUsersAsync(new[] { s.CallerId }, "CallAccepted", new CallAcceptedEvent(callId));
    }

    public async Task RejectAsync(Guid callId, Guid userId)
    {
        var s = tracker.Get(callId) ?? throw DomainException.NotFound("Call not found");
        if (s.CalleeId != userId) throw DomainException.Forbidden("Only the callee can decline");
        tracker.Remove(callId);
        await chat.AddCallMessageAsync(s.ConversationId, s.CallerId, s.Media, "Declined", 0);
        await rt.SendToUsersAsync(new[] { s.CallerId }, "CallEnded", new CallEndedEvent(callId, "Declined"));
    }

    /// <summary>Hang up. Idempotent: if both sides hang up at the same time the second call does nothing.</summary>
    public async Task EndAsync(Guid callId, Guid userId)
    {
        var s = tracker.Get(callId);
        if (s == null) return;
        if (!s.Has(userId)) throw DomainException.Forbidden("Not your call");
        if (tracker.Remove(callId) == null) return;
        await FinishAsync(s, userId);
    }

    public async Task EndAllForUserAsync(Guid userId)
    {
        foreach (var s in tracker.RemoveAllFor(userId)) await FinishAsync(s, userId);
    }

    private async Task FinishAsync(CallSession s, Guid endedBy)
    {
        var status = s.AcceptedAt == null ? "Missed" : "Ended";
        var seconds = s.AcceptedAt == null ? 0 : (int)(DateTime.UtcNow - s.AcceptedAt.Value).TotalSeconds;
        await chat.AddCallMessageAsync(s.ConversationId, s.CallerId, s.Media, status, seconds);
        await rt.SendToUsersAsync(new[] { s.Other(endedBy) }, "CallEnded", new CallEndedEvent(s.CallId, status));
    }

    /// <summary>Relays WebRTC offer / answer / ICE candidates to the other participant.</summary>
    public async Task SignalAsync(Guid callId, Guid userId, string kind, string payload)
    {
        if (kind != "offer" && kind != "answer" && kind != "candidate") throw DomainException.Validation("Unknown signal");
        if (payload.Length > 20000) throw DomainException.Validation("Signal too large");
        var s = tracker.Get(callId);
        if (s == null) return;
        if (!s.Has(userId)) throw DomainException.Forbidden("Not your call");
        await rt.SendToUsersAsync(new[] { s.Other(userId) }, "CallSignal", new CallSignalEvent(callId, userId, kind, payload));
    }
}
