namespace Djama.Api.Domain;

public enum ConversationType { Direct = 0, Group = 1 }
public enum MemberRole { Member = 0, Admin = 1 }
public enum NotificationType { NewMessage = 0, AddedToGroup = 1, System = 2 }
public enum MessageKind { Text = 0, Image = 1, File = 2, Voice = 3, Call = 4 }

// NB: the entity is called AppUser (not User) to avoid a clash with ControllerBase.User.
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";      // lowercase, unique
    public string Email { get; set; } = "";         // lowercase, unique
    public string DisplayName { get; set; } = "";
    public string? About { get; set; }
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
}

public class Contact
{
    public Guid OwnerId { get; set; }
    public Guid ContactUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AppUser? ContactUser { get; set; }
}

public class Block
{
    public Guid BlockerId { get; set; }
    public Guid BlockedUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public AppUser? BlockedUser { get; set; }
}

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ConversationType Type { get; set; }
    public string? Title { get; set; }              // groups only
    public string? DirectKey { get; set; }          // "<smallerId>_<largerId>" for direct chats (unique)
    public Guid CreatedById { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public List<ConversationMember> Members { get; set; } = new();
    public List<Message> Messages { get; set; } = new();
}

public class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public MemberRole Role { get; set; } = MemberRole.Member;
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;
    public Conversation? Conversation { get; set; }
    public AppUser? User { get; set; }
}

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; }
    public MessageKind Kind { get; set; } = MessageKind.Text;
    public Guid? ReplyToId { get; set; }            // the message this one answers (quote)
    public Message? ReplyTo { get; set; }
    public DateTime? EditedAt { get; set; }
    public string? CallMedia { get; set; }          // Kind = Call: "Audio" | "Video"
    public string? CallStatus { get; set; }         // Kind = Call: "Ended" | "Missed" | "Declined"
    public int CallSeconds { get; set; }            // Kind = Call: duration when Ended
    public Conversation? Conversation { get; set; }
    public AppUser? Sender { get; set; }
    public List<Reaction> Reactions { get; set; } = new();
    public List<MessageRead> Reads { get; set; } = new();
    public List<Attachment> Attachments { get; set; } = new();
}

public class MessageRead
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public DateTime ReadAt { get; set; } = DateTime.UtcNow;
}

public class Reaction
{
    public Guid MessageId { get; set; }
    public Guid UserId { get; set; }
    public string Emoji { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MessageId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string StoragePath { get; set; } = "";
    public int? DurationMs { get; set; }            // voice messages
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";         // who/what (sender name, ...)
    public string Body { get; set; } = "";          // message preview / group title
    public Guid? ConversationId { get; set; }
    public bool IsRead { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
