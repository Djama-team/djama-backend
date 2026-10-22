using Djama.Api.Data;
using Djama.Api.Domain;
using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Controllers;

[ApiController, Authorize, Route("api")]
public class AttachmentsController(DjamaDbContext db, ChatService chat, IConfiguration cfg, IWebHostEnvironment env) : ControllerBase
{
    // Only these types are ever displayed inline by the browser; everything else is forced to "download" (anti-XSS).
    private static readonly HashSet<string> Inline = new()
    {
        "image/jpeg", "image/png", "image/gif", "image/webp",
        "audio/webm", "audio/ogg", "audio/mpeg", "audio/mp4", "audio/wav", "audio/x-m4a",
        "video/mp4", "video/webm"
    };

    private string Root => Path.GetFullPath(Path.Combine(env.ContentRootPath, cfg["Storage:Path"] ?? "uploads"));
    private long MaxBytes => cfg.GetValue("Storage:MaxFileMb", 25) * 1024L * 1024L;

    /// <summary>multipart/form-data: files[] (1-10), caption?, replyToId?, voice? (true for voice notes), durationMs?</summary>
    [HttpPost("conversations/{id:guid}/attachments")]
    [RequestSizeLimit(60_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 60_000_000)]
    public async Task<ActionResult<List<MessageDto>>> Upload(Guid id, [FromForm] List<IFormFile> files, [FromForm] string? caption,
        [FromForm] Guid? replyToId, [FromForm] bool voice = false, [FromForm] int? durationMs = null)
    {
        var me = User.GetUserId();
        if (!await chat.IsMemberAsync(id, me)) throw DomainException.Forbidden("Not a member of this conversation");
        if (files == null || files.Count == 0 || files.Count > 10) throw DomainException.Validation("Send between 1 and 10 files");

        var dir = Path.Combine(Root, id.ToString());
        Directory.CreateDirectory(dir);
        var saved = new List<NewAttachment>();
        try
        {
            foreach (var f in files)
            {
                if (f.Length == 0 || f.Length > MaxBytes) throw DomainException.Validation($"Each file must be smaller than {MaxBytes / 1024 / 1024} MB");
                var ct = (string.IsNullOrWhiteSpace(f.ContentType) ? "application/octet-stream" : f.ContentType).Split(';')[0].Trim().ToLowerInvariant();
                var kind = voice && ct.StartsWith("audio/") ? MessageKind.Voice
                    : ct is "image/jpeg" or "image/png" or "image/gif" or "image/webp" ? MessageKind.Image
                    : MessageKind.File;
                var ext = Path.GetExtension(f.FileName);
                if (ext.Length > 10) ext = "";
                var storedName = Guid.NewGuid().ToString("N") + ext;
                await using (var fs = System.IO.File.Create(Path.Combine(dir, storedName))) await f.CopyToAsync(fs);
                saved.Add(new NewAttachment(kind, Path.GetFileName(f.FileName), ct, f.Length,
                    Path.Combine(id.ToString(), storedName), kind == MessageKind.Voice ? durationMs : null));
            }
            return await chat.SendAttachmentsAsync(id, me, saved, caption, replyToId);
        }
        catch
        {
            foreach (var s in saved) { try { System.IO.File.Delete(Path.Combine(Root, s.StoragePath)); } catch { /* best effort */ } }
            throw;
        }
    }

    /// <summary>Members only. Browsers cannot send headers from img/audio tags, so ?access_token= is accepted here (see Program.cs).</summary>
    [HttpGet("attachments/{id:guid}")]
    public async Task<IActionResult> Download(Guid id)
    {
        var me = User.GetUserId();
        var a = await db.Attachments.FirstOrDefaultAsync(x => x.Id == id) ?? throw DomainException.NotFound("File not found");
        var msg = await db.Messages.FirstOrDefaultAsync(m => m.Id == a.MessageId) ?? throw DomainException.NotFound("File not found");
        if (msg.IsDeleted) throw DomainException.NotFound("File not found");
        if (!await chat.IsMemberAsync(msg.ConversationId, me)) throw DomainException.Forbidden("Not a member of this conversation");

        var full = Path.GetFullPath(Path.Combine(Root, a.StoragePath));
        if (!full.StartsWith(Root) || !System.IO.File.Exists(full)) throw DomainException.NotFound("File not found");
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return Inline.Contains(a.ContentType)
            ? PhysicalFile(full, a.ContentType, enableRangeProcessing: true)
            : PhysicalFile(full, "application/octet-stream", a.FileName, enableRangeProcessing: true);
    }
}
