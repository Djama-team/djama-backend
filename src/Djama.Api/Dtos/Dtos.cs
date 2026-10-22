namespace Djama.Api.Dtos;

// ===== Requests (client -> server) =====
public record RegisterRequest(string Username, string DisplayName, string Email, string Password);
public record LoginRequest(string UsernameOrEmail, string Password);
public record UpdateProfileRequest(string DisplayName, string? About);
public record UserIdRequest(Guid UserId);                       // contacts, blocks, direct chat, add member
public record CreateGroupRequest(string Title, List<Guid> MemberIds);
public record RenameRequest(string Title);
public record ChangeRoleRequest(string Role);                   // "Admin" | "Member"
public record SendMessageRequest(string Text, Guid? ReplyToId = null);
public record EditMessageRequest(string Text);
public record ReactRequest(string Emoji);

// ===== Responses (server -> client) =====
public record UserDto(Guid Id, string Username, string DisplayName, string? About, bool IsOnline, DateTime? LastSeenAt);
public record AuthResponse(string Token, DateTime ExpiresAt, UserDto User);
public record ContactDto(UserDto User, DateTime AddedAt);
public record MemberDto(UserDto User, string Role, DateTime JoinedAt);
public record ReactionDto(string Emoji, int Count, List<Guid> UserIds);
public record AttachmentDto(Guid Id, string FileName, string ContentType, long SizeBytes, int? DurationMs);
public record ReplyPreviewDto(Guid Id, string SenderName, string Text, string Kind, bool IsDeleted);
public record CallInfoDto(string Media, string Status, int Seconds);
public record MessageDto(Guid Id, Guid ConversationId, Guid SenderId, string SenderName, string Kind, string Text,
    DateTime CreatedAt, DateTime? EditedAt, bool IsDeleted, ReplyPreviewDto? ReplyTo, CallInfoDto? Call,
    List<ReactionDto> Reactions, List<Guid> ReadByUserIds, List<AttachmentDto> Attachments);
public record ConversationDto(Guid Id, string Type, string Title, List<MemberDto> Members,
    MessageDto? LastMessage, int UnreadCount, DateTime UpdatedAt);
public record NotificationDto(Guid Id, string Type, string Title, string Body, Guid? ConversationId, bool IsRead, DateTime CreatedAt);

// ===== SignalR events (server -> client) =====
public record MessagesReadEvent(Guid ConversationId, Guid UserId, DateTime ReadAt);
public record MessageDeletedEvent(Guid ConversationId, Guid MessageId);
public record ReactionChangedEvent(Guid ConversationId, Guid MessageId, List<ReactionDto> Reactions);
public record TypingEvent(Guid ConversationId, Guid UserId);
public record PresenceEvent(Guid UserId, bool IsOnline);
public record ConversationChangedEvent(Guid ConversationId);
public record IncomingCallEvent(Guid CallId, Guid ConversationId, UserDto Caller, string Media);
public record CallAcceptedEvent(Guid CallId);
public record CallEndedEvent(Guid CallId, string Reason);        // Ended | Missed | Declined
public record CallSignalEvent(Guid CallId, Guid FromUserId, string Kind, string Payload);   // offer | answer | candidate
