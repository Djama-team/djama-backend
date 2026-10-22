using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Data;

/// <summary>
/// v1 -> v2 without losing data: EnsureCreated() never alters an existing database, so the new v2 columns are
/// added here (idempotent, safe to run at every start). Once P2 switches to EF migrations this class can be deleted.
/// </summary>
public static class DbUpgrade
{
    public static void Apply(DjamaDbContext db)
    {
        db.Database.ExecuteSqlRaw("""
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS "Kind" varchar(16) NOT NULL DEFAULT 'Text';
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS "ReplyToId" uuid NULL;
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS "EditedAt" timestamptz NULL;
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS "CallMedia" varchar(16) NULL;
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS "CallStatus" varchar(16) NULL;
            ALTER TABLE messages ADD COLUMN IF NOT EXISTS "CallSeconds" integer NOT NULL DEFAULT 0;
            ALTER TABLE attachments ADD COLUMN IF NOT EXISTS "DurationMs" integer NULL;
            """);
    }
}
