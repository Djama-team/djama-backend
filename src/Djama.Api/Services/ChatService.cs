using Djama.Api.Data;
using Djama.Api.Domain;
using Djama.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Services;

/// <summary>All chat business rules live here. Used by REST controllers AND by the SignalR hub.</summary>
public class ChatService(DjamaDbContext db, IRealtimeNotifier rt, PresenceTracker presence)
{
    // ======================= mapping =======================
    public UserDto ToUserDto(AppUser u) =>
        new(u.Id, u.Username, u.DisplayName, u.About, presence.IsOnline(u.Id), u.LastSeenAt);

    public static NotificationDto ToNotificationDto(Notification n) =>
        new(n.Id, n.Type.ToString(), n.Title, n.Body, n.ConversationId, n.IsRead, n.CreatedAt);

    public static List<ReactionDto> GroupReactions(IEnumerable<Reaction> rs) =>
        rs.GroupBy(r => r.Emoji).Select(g => new ReactionDto(g.Key, g.Count(), g.Select(x => x.UserId).ToList())).ToList();

    private static string Preview(string t) => t.Length <= 120 ? t : t[..120] + "...";

    public static MessageDto ToMessageDto(Message m) =>
        new(m.Id, m.ConversationId, m.SenderId, m.Sender?.DisplayName ?? "", m.Kind.ToString(),
            m.IsDeleted ? "" : m.Text, m.CreatedAt, m.EditedAt, m.IsDeleted,
            m.ReplyTo == null ? null : new ReplyPreviewDto(m.ReplyTo.Id, m.ReplyTo.Sender?.DisplayName ?? "",
                m.ReplyTo.IsDeleted ? "" : Preview(m.ReplyTo.Text), m.ReplyTo.Kind.ToString(), m.ReplyTo.IsDeleted),
            m.Kind == MessageKind.Call ? new CallInfoDto(m.CallMedia ?? "Audio", m.CallStatus ?? "Ended", m.CallSeconds) : null,
            GroupReactions(m.Reactions), m.Reads.Select(r => r.UserId).ToList(),
            m.Attachments.Select(a => new AttachmentDto(a.Id, a.FileName, a.ContentType, a.SizeBytes, a.DurationMs)).ToList());

    private IQueryable<Message> MessagesQuery() =>
        db.Messages.Include(m => m.Sender).Include(m => m.Reactions).Include(m => m.Reads).Include(m => m.Attachments)
            .Include(m => m.ReplyTo).ThenInclude(r => r!.Sender).AsSplitQuery();

    private Task<Conversation?> LoadConversationAsync(Guid id) =>
        db.Conversations.Include(c => c.Members).ThenInclude(m => m.User).AsSplitQuery().FirstOrDefaultAsync(c => c.Id == id);

    private async Task<Conversation> LoadGroupAsync(Guid id)
    {
        var conv = await db.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == id)
                   ?? throw DomainException.NotFound("Conversation not found");
        if (conv.Type != ConversationType.Group) throw DomainException.Validation("Not a group conversation");
        return conv;
    }

    private static void RequireAdmin(Conversation conv, Guid actorId)
    {
        if (!conv.Members.Any(m => m.UserId == actorId && m.Role == MemberRole.Admin))
            throw DomainException.Forbidden("Only group admins can do this");
    }

    // ======================= helpers =======================
    public static string DirectKey(Guid a, Guid b) =>
        string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? $"{a}_{b}" : $"{b}_{a}";

    public Task<bool> IsBlockedBetweenAsync(Guid a, Guid b) =>
        db.Blocks.AnyAsync(x => (x.BlockerId == a && x.BlockedUserId == b) || (x.BlockerId == b && x.BlockedUserId == a));

    public Task<bool> IsMemberAsync(Guid convId, Guid userId) =>
        db.ConversationMembers.AnyAsync(m => m.ConversationId == convId && m.UserId == userId);

    private async Task EnsureMemberAsync(Guid convId, Guid userId)
    {
        if (!await IsMemberAsync(convId, userId)) throw DomainException.Forbidden("Not a member of this conversation");
    }

    private Task<List<Guid>> MemberIdsAsync(Guid convId) =>
        db.ConversationMembers.Where(m => m.ConversationId == convId).Select(m => m.UserId).ToListAsync();

    private Task PushChangedAsync(Guid convId, IEnumerable<Guid> userIds) =>
        rt.SendToUsersAsync(userIds, "ConversationChanged", new ConversationChangedEvent(convId));

    private Task PushNotificationsAsync(IEnumerable<Notification> notes) =>
        Task.WhenAll(notes.Select(n => rt.SendToUsersAsync(new[] { n.UserId }, "NotificationReceived", ToNotificationDto(n))));

    private Task<string> DisplayNameAsync(Guid userId) =>
        db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).FirstAsync();

    // ======================= presence =======================
    public async Task<List<Guid>> GetPeerIdsAsync(Guid userId)
    {
        var convIds = db.ConversationMembers.Where(m => m.UserId == userId).Select(m => m.ConversationId);
        return await db.ConversationMembers.Where(m => convIds.Contains(m.ConversationId) && m.UserId != userId)
            .Select(m => m.UserId).Distinct().ToListAsync();
    }

    public async Task BroadcastPresenceAsync(Guid userId, bool online) =>
        await rt.SendToUsersAsync(await GetPeerIdsAsync(userId), "PresenceChanged", new PresenceEvent(userId, online));

    public async Task SetLastSeenAsync(Guid userId)
    {
        var u = await db.Users.FindAsync(userId);
        if (u == null) return;
        u.LastSeenAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task NotifyTypingAsync(Guid convId, Guid userId)
    {
        await EnsureMemberAsync(convId, userId);
        var others = (await MemberIdsAsync(convId)).Where(i => i != userId);
        await rt.SendToUsersAsync(others, "Typing", new TypingEvent(convId, userId));
    }

    // ======================= conversations =======================
    public async Task<ConversationDto> ToConversationDtoAsync(Conversation c, Guid viewerId)
    {
        var members = c.Members.OrderBy(m => m.JoinedAt)
            .Select(m => new MemberDto(ToUserDto(m.User!), m.Role.ToString(), m.JoinedAt)).ToList();
        var title = c.Type == ConversationType.Group
            ? c.Title ?? ""
            : c.Members.FirstOrDefault(m => m.UserId != viewerId)?.User?.DisplayName ?? "";
        var last = await MessagesQuery().Where(m => m.ConversationId == c.Id)
            .OrderByDescending(m => m.CreatedAt).FirstOrDefaultAsync();
        var unread = await db.Messages.CountAsync(m => m.ConversationId == c.Id && m.SenderId != viewerId
            && !m.IsDeleted && !m.Reads.Any(r => r.UserId == viewerId));
        return new ConversationDto(c.Id, c.Type.ToString(), title, members,
            last == null ? null : ToMessageDto(last), unread, c.UpdatedAt);
    }

    public async Task<List<ConversationDto>> ListConversationsAsync(Guid userId)
    {
        var convs = await db.Conversations
            .Where(c => c.Members.Any(m => m.UserId == userId)
                        && (c.Type == ConversationType.Group || c.CreatedById == userId || c.Messages.Any()))
            .Include(c => c.Members).ThenInclude(m => m.User)
            .AsSplitQuery().OrderByDescending(c => c.UpdatedAt).ToListAsync();
        var result = new List<ConversationDto>();
        foreach (var c in convs) result.Add(await ToConversationDtoAsync(c, userId));   // TODO(P2): avoid N+1 queries
        return result;
    }

    public async Task<ConversationDto> GetConversationAsync(Guid convId, Guid viewerId)
    {
        var conv = await LoadConversationAsync(convId) ?? throw DomainException.NotFound("Conversation not found");
        if (conv.Members.All(m => m.UserId != viewerId)) throw DomainException.Forbidden("Not a member of this conversation");
        return await ToConversationDtoAsync(conv, viewerId);
    }

    public async Task<ConversationDto> GetOrCreateDirectAsync(Guid me, Guid otherId)
    {
        if (otherId == me) throw DomainException.Validation("You cannot chat with yourself");
        if (!await db.Users.AnyAsync(u => u.Id == otherId)) throw DomainException.NotFound("User not found");
        if (await IsBlockedBetweenAsync(me, otherId)) throw DomainException.Blocked();
        var key = DirectKey(me, otherId);
        var conv = await db.Conversations.FirstOrDefaultAsync(c => c.DirectKey == key);
        if (conv == null)
        {
            conv = new Conversation { Type = ConversationType.Direct, DirectKey = key, CreatedById = me };
            conv.Members.Add(new ConversationMember { UserId = me });
            conv.Members.Add(new ConversationMember { UserId = otherId });
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
        }
        return await GetConversationAsync(conv.Id, me);
    }

    public async Task<ConversationDto> CreateGroupAsync(Guid me, string? title, List<Guid>? memberIds)
    {
        title = title?.Trim() ?? "";
        if (title.Length is < 1 or > 64) throw DomainException.Validation("Group title must be 1-64 characters");
        var ids = (memberIds ?? new List<Guid>()).Where(i => i != me).Distinct().ToList();
        if (ids.Count > 200) throw DomainException.Validation("Too many members (max 200)");
        var found = await db.Users.Where(u => ids.Contains(u.Id)).Select(u => u.Id).ToListAsync();
        if (found.Count != ids.Count) throw DomainException.NotFound("Some users do not exist");

        var actorName = await DisplayNameAsync(me);
        var conv = new Conversation { Type = ConversationType.Group, Title = title, CreatedById = me };
        conv.Members.Add(new ConversationMember { UserId = me, Role = MemberRole.Admin });
        foreach (var id in ids) conv.Members.Add(new ConversationMember { UserId = id });
        var notes = ids.Select(id => new Notification
        {
            UserId = id, Type = NotificationType.AddedToGroup, Title = actorName, Body = title, ConversationId = conv.Id
        }).ToList();
        db.Conversations.Add(conv);
        db.Notifications.AddRange(notes);
        await db.SaveChangesAsync();

        await PushChangedAsync(conv.Id, ids.Append(me));
        await PushNotificationsAsync(notes);
        return await GetConversationAsync(conv.Id, me);
    }

    public async Task<ConversationDto> RenameGroupAsync(Guid convId, Guid actorId, string? title)
    {
        var conv = await LoadGroupAsync(convId);
        RequireAdmin(conv, actorId);
        title = title?.Trim() ?? "";
        if (title.Length is < 1 or > 64) throw DomainException.Validation("Group title must be 1-64 characters");
        conv.Title = title;
        await db.SaveChangesAsync();
        await PushChangedAsync(convId, conv.Members.Select(m => m.UserId));
        return await GetConversationAsync(convId, actorId);
    }

    public async Task<ConversationDto> AddMemberAsync(Guid convId, Guid actorId, Guid userId)
    {
        var conv = await LoadGroupAsync(convId);
        RequireAdmin(conv, actorId);
        if (conv.Members.Any(m => m.UserId == userId)) throw DomainException.Conflict("Already a member");
        if (!await db.Users.AnyAsync(u => u.Id == userId)) throw DomainException.NotFound("User not found");

        db.ConversationMembers.Add(new ConversationMember { ConversationId = convId, UserId = userId });
        var note = new Notification
        {
            UserId = userId, Type = NotificationType.AddedToGroup, Title = await DisplayNameAsync(actorId),
            Body = conv.Title ?? "", ConversationId = convId
        };
        db.Notifications.Add(note);
        await db.SaveChangesAsync();

        await PushChangedAsync(convId, conv.Members.Select(m => m.UserId).Append(userId).Distinct());
        await PushNotificationsAsync(new[] { note });
        return await GetConversationAsync(convId, actorId);
    }

    public async Task RemoveMemberAsync(Guid convId, Guid actorId, Guid userId)
    {
        var conv = await LoadGroupAsync(convId);
        if (conv.Members.All(m => m.UserId != actorId)) throw DomainException.Forbidden("Not a member of this conversation");
        if (actorId != userId) RequireAdmin(conv, actorId);          // leaving is always allowed
        var target = conv.Members.FirstOrDefault(m => m.UserId == userId) ?? throw DomainException.NotFound("Member not found");

        var affected = conv.Members.Select(m => m.UserId).ToList();
        var remaining = conv.Members.Where(m => m.UserId != userId).ToList();
        db.ConversationMembers.Remove(target);
        if (remaining.Count == 0) db.Conversations.Remove(conv);                       // last one out: delete the group
        else if (!remaining.Any(m => m.Role == MemberRole.Admin))
            remaining.OrderBy(m => m.JoinedAt).First().Role = MemberRole.Admin;        // never leave a group without admin
        await db.SaveChangesAsync();
        await PushChangedAsync(convId, affected);
    }

    public async Task<ConversationDto> ChangeRoleAsync(Guid convId, Guid actorId, Guid userId, string? role)
    {
        var conv = await LoadGroupAsync(convId);
        RequireAdmin(conv, actorId);
        var target = conv.Members.FirstOrDefault(m => m.UserId == userId) ?? throw DomainException.NotFound("Member not found");
        if (!Enum.TryParse<MemberRole>(role, true, out var newRole)) throw DomainException.Validation("Role must be Admin or Member");
        if (newRole == MemberRole.Member && target.Role == MemberRole.Admin && conv.Members.Count(m => m.Role == MemberRole.Admin) <= 1)
            throw DomainException.Validation("A group needs at least one admin");
        target.Role = newRole;
        await db.SaveChangesAsync();
        await PushChangedAsync(convId, conv.Members.Select(m => m.UserId));
        return await GetConversationAsync(convId, actorId);
    }

    // ======================= messages =======================
    private async Task<Conversation> LoadForSendAsync(Guid convId, Guid senderId)
    {
        var conv = await db.Conversations.Include(c => c.Members).FirstOrDefaultAsync(c => c.Id == convId)
                   ?? throw DomainException.NotFound("Conversation not found");
        if (conv.Members.All(m => m.UserId != senderId)) throw DomainException.Forbidden("Not a member of this conversation");
        if (conv.Type == ConversationType.Direct)
        {
            var otherId = conv.Members.First(m => m.UserId != senderId).UserId;
            if (await IsBlockedBetweenAsync(senderId, otherId)) throw DomainException.Blocked();
        }
        return conv;
    }

    private async Task EnsureReplyTargetAsync(Guid convId, Guid? replyToId)
    {
        if (replyToId.HasValue && !await db.Messages.AnyAsync(m => m.Id == replyToId.Value && m.ConversationId == convId))
            throw DomainException.Validation("The message you reply to does not exist in this conversation");
    }

    /// <summary>Saves a message (+ "missed message" notifications for offline members) and broadcasts it to every member.</summary>
    private async Task<MessageDto> StoreAndBroadcastAsync(Conversation conv, Message msg, string notificationBody, bool notify = true)
    {
        db.Messages.Add(msg);
        conv.UpdatedAt = msg.CreatedAt;
        var notes = new List<Notification>();
        if (notify)
        {
            var senderName = await DisplayNameAsync(msg.SenderId);
            notes = conv.Members.Where(m => m.UserId != msg.SenderId && !presence.IsOnline(m.UserId))
                .Select(m => new Notification
                {
                    UserId = m.UserId, Type = NotificationType.NewMessage, Title = senderName,
                    Body = notificationBody, ConversationId = conv.Id
                }).ToList();
            db.Notifications.AddRange(notes);
        }
        await db.SaveChangesAsync();
        var dto = ToMessageDto(await MessagesQuery().FirstAsync(m => m.Id == msg.Id));
        await rt.SendToUsersAsync(conv.Members.Select(m => m.UserId), "ReceiveMessage", dto);
        await PushNotificationsAsync(notes);
        return dto;
    }

    public async Task<MessageDto> SendMessageAsync(Guid convId, Guid senderId, string? text, Guid? replyToId = null)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) throw DomainException.Validation("Message is empty");
        if (text.Length > 4000) throw DomainException.Validation("Message is too long (max 4000 characters)");
        var conv = await LoadForSendAsync(convId, senderId);
        await EnsureReplyTargetAsync(convId, replyToId);
        return await StoreAndBroadcastAsync(conv,
            new Message { ConversationId = convId, SenderId = senderId, Text = text, ReplyToId = replyToId }, Preview(text));
    }

    /// <summary>One message per uploaded file (photo, document or voice note). The caption goes with the first file.</summary>
    public async Task<List<MessageDto>> SendAttachmentsAsync(Guid convId, Guid senderId, IReadOnlyList<NewAttachment> files, string? caption, Guid? replyToId)
    {
        if (files.Count == 0) throw DomainException.Validation("No file");
        caption = caption?.Trim() ?? "";
        if (caption.Length > 1000) throw DomainException.Validation("Caption is too long (max 1000 characters)");
        var conv = await LoadForSendAsync(convId, senderId);
        await EnsureReplyTargetAsync(convId, replyToId);
        var result = new List<MessageDto>();
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var msg = new Message
            {
                ConversationId = convId, SenderId = senderId, Kind = f.Kind,
                Text = i == 0 ? caption : "", ReplyToId = i == 0 ? replyToId : null
            };
            msg.Attachments.Add(new Attachment
            {
                FileName = f.FileName, ContentType = f.ContentType, SizeBytes = f.SizeBytes,
                StoragePath = f.StoragePath, DurationMs = f.DurationMs
            });
            var label = f.Kind == MessageKind.Image ? "Photo" : f.Kind == MessageKind.Voice ? "Voice message" : f.FileName;
            result.Add(await StoreAndBroadcastAsync(conv, msg, label));
        }
        return result;
    }

    /// <summary>Written by CallService when a call ends: shows "Missed call", "Video call 2:13"... in the chat.</summary>
    public async Task AddCallMessageAsync(Guid convId, Guid callerId, string media, string status, int seconds)
    {
        var conv = await db.Conversations.Include(c => c.Members).FirstAsync(c => c.Id == convId);
        await StoreAndBroadcastAsync(conv, new Message
        {
            ConversationId = convId, SenderId = callerId, Kind = MessageKind.Call,
            CallMedia = media, CallStatus = status, CallSeconds = seconds
        }, "", notify: false);
    }

    /// <summary>Authors can edit text/caption messages for 15 minutes.</summary>
    public async Task<MessageDto> EditMessageAsync(Guid messageId, Guid userId, string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0) throw DomainException.Validation("Message is empty");
        if (text.Length > 4000) throw DomainException.Validation("Message is too long (max 4000 characters)");
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId) ?? throw DomainException.NotFound("Message not found");
        if (msg.SenderId != userId) throw DomainException.Forbidden("You can only edit your own messages");
        if (msg.IsDeleted || msg.Kind is MessageKind.Voice or MessageKind.Call) throw DomainException.Validation("This message cannot be edited");
        if (DateTime.UtcNow - msg.CreatedAt > TimeSpan.FromMinutes(15)) throw DomainException.Forbidden("Messages can only be edited within 15 minutes");
        msg.Text = text;
        msg.EditedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var dto = ToMessageDto(await MessagesQuery().FirstAsync(m => m.Id == messageId));
        await rt.SendToUsersAsync(await MemberIdsAsync(msg.ConversationId), "MessageEdited", dto);
        return dto;
    }

    public async Task<List<MessageDto>> GetMessagesAsync(Guid convId, Guid userId, DateTime? before, int limit)
    {
        await EnsureMemberAsync(convId, userId);
        limit = Math.Clamp(limit, 1, 100);
        var q = MessagesQuery().Where(m => m.ConversationId == convId);
        if (before.HasValue)
        {
            var b = before.Value.ToUniversalTime();      // PostgreSQL timestamptz needs UTC
            q = q.Where(m => m.CreatedAt < b);
        }
        var list = await q.OrderByDescending(m => m.CreatedAt).Take(limit).ToListAsync();
        list.Reverse();                                  // oldest -> newest
        return list.Select(ToMessageDto).ToList();
    }

    public async Task MarkReadAsync(Guid convId, Guid userId)
    {
        await EnsureMemberAsync(convId, userId);
        var unreadIds = await db.Messages
            .Where(m => m.ConversationId == convId && m.SenderId != userId && !m.Reads.Any(r => r.UserId == userId))
            .Select(m => m.Id).ToListAsync();
        var notes = await db.Notifications.Where(n => n.UserId == userId && n.ConversationId == convId && !n.IsRead).ToListAsync();
        foreach (var n in notes) n.IsRead = true;
        if (unreadIds.Count == 0) { if (notes.Count > 0) await db.SaveChangesAsync(); return; }

        var now = DateTime.UtcNow;
        db.MessageReads.AddRange(unreadIds.Select(id => new MessageRead { MessageId = id, UserId = userId, ReadAt = now }));
        await db.SaveChangesAsync();
        await rt.SendToUsersAsync(await MemberIdsAsync(convId), "MessagesRead", new MessagesReadEvent(convId, userId, now));
    }

    public async Task DeleteMessageAsync(Guid messageId, Guid userId)
    {
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId) ?? throw DomainException.NotFound("Message not found");
        if (msg.SenderId != userId) throw DomainException.Forbidden("You can only delete your own messages");
        if (msg.IsDeleted) return;
        msg.IsDeleted = true;
        msg.Text = "";
        await db.SaveChangesAsync();
        await rt.SendToUsersAsync(await MemberIdsAsync(msg.ConversationId), "MessageDeleted", new MessageDeletedEvent(msg.ConversationId, msg.Id));
    }

    /// <summary>One reaction per user per message: same emoji again removes it, another emoji replaces it.</summary>
    public async Task<List<ReactionDto>> ToggleReactionAsync(Guid messageId, Guid userId, string? emoji)
    {
        emoji = emoji?.Trim() ?? "";
        if (emoji.Length is < 1 or > 16) throw DomainException.Validation("Invalid emoji");
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId) ?? throw DomainException.NotFound("Message not found");
        await EnsureMemberAsync(msg.ConversationId, userId);
        if (msg.IsDeleted) throw DomainException.Validation("Message was deleted");

        var existing = await db.Reactions.FirstOrDefaultAsync(r => r.MessageId == messageId && r.UserId == userId);
        if (existing == null) db.Reactions.Add(new Reaction { MessageId = messageId, UserId = userId, Emoji = emoji });
        else if (existing.Emoji == emoji) db.Reactions.Remove(existing);
        else existing.Emoji = emoji;
        await db.SaveChangesAsync();

        var reactions = GroupReactions(await db.Reactions.Where(r => r.MessageId == messageId).ToListAsync());
        await rt.SendToUsersAsync(await MemberIdsAsync(msg.ConversationId), "ReactionChanged",
            new ReactionChangedEvent(msg.ConversationId, messageId, reactions));
        return reactions;
    }
}

public record NewAttachment(MessageKind Kind, string FileName, string ContentType, long SizeBytes, string StoragePath, int? DurationMs);
