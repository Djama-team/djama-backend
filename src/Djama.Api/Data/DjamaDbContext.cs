using Djama.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Data;

public class DjamaDbContext(DbContextOptions<DjamaDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Block> Blocks => Set<Block>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> ConversationMembers => Set<ConversationMember>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageRead> MessageReads => Set<MessageRead>();
    public DbSet<Reaction> Reactions => Set<Reaction>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>(e =>
        {
            e.ToTable("users");
            e.HasIndex(u => u.Username).IsUnique();
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.Username).HasMaxLength(32);
            e.Property(u => u.Email).HasMaxLength(128);
            e.Property(u => u.DisplayName).HasMaxLength(64);
            e.Property(u => u.About).HasMaxLength(256);
        });

        b.Entity<Contact>(e =>
        {
            e.ToTable("contacts");
            e.HasKey(c => new { c.OwnerId, c.ContactUserId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(c => c.OwnerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.ContactUser).WithMany().HasForeignKey(c => c.ContactUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Block>(e =>
        {
            e.ToTable("blocks");
            e.HasKey(x => new { x.BlockerId, x.BlockedUserId });
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.BlockerId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.BlockedUser).WithMany().HasForeignKey(x => x.BlockedUserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Conversation>(e =>
        {
            e.ToTable("conversations");
            e.Property(c => c.Type).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Title).HasMaxLength(64);
            e.HasIndex(c => c.DirectKey).IsUnique();   // several NULLs (groups) are allowed in PostgreSQL
            e.HasIndex(c => c.UpdatedAt);
        });

        b.Entity<ConversationMember>(e =>
        {
            e.ToTable("conversation_members");
            e.HasKey(m => new { m.ConversationId, m.UserId });
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(16);
            e.HasOne(m => m.Conversation).WithMany(c => c.Members).HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(m => m.UserId);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.Property(m => m.Text).HasMaxLength(4000);
            e.Property(m => m.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.CallMedia).HasMaxLength(16);
            e.Property(m => m.CallStatus).HasMaxLength(16);
            e.HasOne(m => m.ReplyTo).WithMany().HasForeignKey(m => m.ReplyToId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(m => m.Conversation).WithMany(c => c.Messages).HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(m => m.Sender).WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(m => new { m.ConversationId, m.CreatedAt });
        });

        b.Entity<MessageRead>(e =>
        {
            e.ToTable("message_reads");
            e.HasKey(r => new { r.MessageId, r.UserId });
            e.HasOne<Message>().WithMany(m => m.Reads).HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Reaction>(e =>
        {
            e.ToTable("reactions");
            e.HasKey(r => new { r.MessageId, r.UserId });   // one reaction per user per message
            e.Property(r => r.Emoji).HasMaxLength(16);
            e.HasOne<Message>().WithMany(m => m.Reactions).HasForeignKey(r => r.MessageId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Attachment>(e =>
        {
            e.ToTable("attachments");
            e.Property(a => a.FileName).HasMaxLength(255);
            e.Property(a => a.ContentType).HasMaxLength(128);
            e.HasOne<Message>().WithMany(m => m.Attachments).HasForeignKey(a => a.MessageId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Notification>(e =>
        {
            e.ToTable("notifications");
            e.Property(n => n.Type).HasConversion<string>().HasMaxLength(24);
            e.Property(n => n.Title).HasMaxLength(128);
            e.Property(n => n.Body).HasMaxLength(256);
            e.HasOne<AppUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(n => new { n.UserId, n.CreatedAt });
        });
    }
}
