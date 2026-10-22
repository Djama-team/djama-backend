using Djama.Api.Data;
using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Controllers;

[ApiController, Authorize, Route("api/notifications")]
public class NotificationsController(DjamaDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<NotificationDto>>> List()
    {
        var me = User.GetUserId();
        var list = await db.Notifications.Where(n => n.UserId == me).OrderByDescending(n => n.CreatedAt).Take(50).ToListAsync();
        return list.Select(ChatService.ToNotificationDto).ToList();
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id)
    {
        var me = User.GetUserId();
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == me);
        if (n != null) { n.IsRead = true; await db.SaveChangesAsync(); }
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll()
    {
        var me = User.GetUserId();
        var list = await db.Notifications.Where(n => n.UserId == me && !n.IsRead).ToListAsync();
        foreach (var n in list) n.IsRead = true;
        await db.SaveChangesAsync();
        return NoContent();
    }
}
