using Djama.Api.Data;
using Djama.Api.Domain;
using Djama.Api.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Djama.Tests;

public class FakeNotifier : IRealtimeNotifier
{
    public List<(string Method, object Payload)> Sent { get; } = new();
    public Task SendToUsersAsync(IEnumerable<Guid> userIds, string method, object payload)
    {
        Sent.Add((method, payload));
        return Task.CompletedTask;
    }
}

public class ChatServiceTests
{
    private static (DjamaDbContext Db, ChatService Svc, FakeNotifier Rt) Create()
    {
        var options = new DbContextOptionsBuilder<DjamaDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new DjamaDbContext(options);
        var rt = new FakeNotifier();
        return (db, new ChatService(db, rt, new PresenceTracker()), rt);
    }

    private static async Task<AppUser> AddUser(DjamaDbContext db, string name)
    {
        var u = new AppUser { Username = name, Email = name + "@test.io", DisplayName = name, PasswordHash = "x" };
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u;
    }

    [Fact]
    public async Task SendMessage_InDirectChat_IsStoredBroadcastAndNotified()
    {
        var (db, svc, rt) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var conv = await svc.GetOrCreateDirectAsync(a.Id, b.Id);

        var msg = await svc.SendMessageAsync(conv.Id, a.Id, "  Salam  ");

        Assert.Equal("Salam", msg.Text);
        Assert.Contains(rt.Sent, s => s.Method == "ReceiveMessage");
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == b.Id));   // bob is offline
    }

    [Fact]
    public async Task SendMessage_ByNonMember_IsForbidden()
    {
        var (db, svc, _) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var c = await AddUser(db, "carol");
        var conv = await svc.GetOrCreateDirectAsync(a.Id, b.Id);

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.SendMessageAsync(conv.Id, c.Id, "hi"));
        Assert.Equal("FORBIDDEN", ex.Code);
    }

    [Fact]
    public async Task SendMessage_WhenBlocked_IsRejected()
    {
        var (db, svc, _) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var conv = await svc.GetOrCreateDirectAsync(a.Id, b.Id);
        db.Blocks.Add(new Block { BlockerId = b.Id, BlockedUserId = a.Id });
        await db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.SendMessageAsync(conv.Id, a.Id, "hi"));
        Assert.Equal("BLOCKED", ex.Code);
    }

    [Fact]
    public async Task SendMessage_Empty_IsRejected()
    {
        var (db, svc, _) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var conv = await svc.GetOrCreateDirectAsync(a.Id, b.Id);

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.SendMessageAsync(conv.Id, a.Id, "   "));
        Assert.Equal("VALIDATION", ex.Code);
    }

    [Fact]
    public async Task MarkRead_ResetsUnreadCount()
    {
        var (db, svc, _) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var conv = await svc.GetOrCreateDirectAsync(a.Id, b.Id);
        await svc.SendMessageAsync(conv.Id, a.Id, "one");
        await svc.SendMessageAsync(conv.Id, a.Id, "two");

        Assert.Equal(2, (await svc.ListConversationsAsync(b.Id)).Single().UnreadCount);
        await svc.MarkReadAsync(conv.Id, b.Id);
        Assert.Equal(0, (await svc.ListConversationsAsync(b.Id)).Single().UnreadCount);
    }

    [Fact]
    public async Task Group_LastAdminLeaving_PromotesAnotherMember()
    {
        var (db, svc, _) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var group = await svc.CreateGroupAsync(a.Id, "Famille", new List<Guid> { b.Id });

        await svc.RemoveMemberAsync(group.Id, a.Id, a.Id);

        var left = await svc.GetConversationAsync(group.Id, b.Id);
        Assert.Single(left.Members);
        Assert.Equal("Admin", left.Members[0].Role);
    }

    [Fact]
    public async Task EditMessage_OnlyByAuthor_AndMarksEdited()
    {
        var (db, svc, _) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var conv = await svc.GetOrCreateDirectAsync(a.Id, b.Id);
        var msg = await svc.SendMessageAsync(conv.Id, a.Id, "helo");

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.EditMessageAsync(msg.Id, b.Id, "hack"));
        Assert.Equal("FORBIDDEN", ex.Code);
        var edited = await svc.EditMessageAsync(msg.Id, a.Id, "hello");
        Assert.Equal("hello", edited.Text);
        Assert.NotNull(edited.EditedAt);
    }

    [Fact]
    public async Task Reply_ToMessageOfAnotherConversation_IsRejected()
    {
        var (db, svc, _) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var direct = await svc.GetOrCreateDirectAsync(a.Id, b.Id);
        var group = await svc.CreateGroupAsync(a.Id, "Famille", new List<Guid> { b.Id });
        var original = await svc.SendMessageAsync(direct.Id, a.Id, "hello");

        var ex = await Assert.ThrowsAsync<DomainException>(() => svc.SendMessageAsync(group.Id, a.Id, "re", original.Id));
        Assert.Equal("VALIDATION", ex.Code);
        var ok = await svc.SendMessageAsync(direct.Id, b.Id, "re", original.Id);
        Assert.Equal(original.Id, ok.ReplyTo!.Id);
    }

    [Fact]
    public async Task Call_ToOfflineUser_RecordsMissedCall()
    {
        var (db, svc, rt) = Create();
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        var conv = await svc.GetOrCreateDirectAsync(a.Id, b.Id);
        var calls = new CallService(db, svc, rt, new CallTracker(), new PresenceTracker());

        var ex = await Assert.ThrowsAsync<DomainException>(() => calls.InviteAsync(a.Id, conv.Id, "Audio"));
        Assert.Equal("OFFLINE", ex.Code);
        Assert.Equal(1, await db.Messages.CountAsync(m => m.Kind == MessageKind.Call && m.CallStatus == "Missed"));
    }

    [Fact]
    public async Task Call_AcceptedThenEnded_IsRecordedAsEnded()
    {
        var options = new DbContextOptionsBuilder<DjamaDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new DjamaDbContext(options);
        var rt = new FakeNotifier();
        var presence = new PresenceTracker();
        var chat = new ChatService(db, rt, presence);
        var calls = new CallService(db, chat, rt, new CallTracker(), presence);
        var a = await AddUser(db, "alice");
        var b = await AddUser(db, "bob");
        presence.Connected(b.Id);                                  // bob is online
        var conv = await chat.GetOrCreateDirectAsync(a.Id, b.Id);

        var callId = await calls.InviteAsync(a.Id, conv.Id, "Video");
        Assert.Contains(rt.Sent, s => s.Method == "IncomingCall");
        await calls.AcceptAsync(callId, b.Id);
        await calls.EndAsync(callId, a.Id);

        var m = await db.Messages.SingleAsync(x => x.Kind == MessageKind.Call);
        Assert.Equal("Ended", m.CallStatus);
        Assert.Equal("Video", m.CallMedia);
    }
}
