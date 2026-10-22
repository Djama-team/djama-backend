using Djama.Api.Data;
using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Controllers;

[ApiController, Authorize, Route("api/users")]
public class UsersController(DjamaDbContext db, ChatService chat) : ControllerBase
{
    /// <summary>Search by username or display name (min 2 chars). Users who blocked me are hidden.</summary>
    [HttpGet("search")]
    public async Task<ActionResult<List<UserDto>>> Search([FromQuery] string? q)
    {
        var me = User.GetUserId();
        q = (q ?? "").Trim().ToLower();
        if (q.Length < 2) return new List<UserDto>();
        var users = await db.Users
            .Where(u => u.Id != me
                        && (u.Username.ToLower().Contains(q) || u.DisplayName.ToLower().Contains(q))
                        && !db.Blocks.Any(b => b.BlockerId == u.Id && b.BlockedUserId == me))
            .OrderBy(u => u.DisplayName).Take(20).ToListAsync();
        return users.Select(chat.ToUserDto).ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<UserDto>> Get(Guid id)
    {
        var user = await db.Users.FindAsync(id) ?? throw DomainException.NotFound("User not found");
        return chat.ToUserDto(user);
    }

    [HttpPut("me")]
    public async Task<ActionResult<UserDto>> UpdateMe(UpdateProfileRequest req)
    {
        var user = await db.Users.FindAsync(User.GetUserId()) ?? throw DomainException.NotFound("User not found");
        var display = (req.DisplayName ?? "").Trim();
        if (display.Length is < 1 or > 64) throw DomainException.Validation("Display name: 1-64 characters");
        var about = req.About?.Trim();
        if (about is { Length: > 256 }) throw DomainException.Validation("About: 256 characters max");
        user.DisplayName = display;
        user.About = string.IsNullOrEmpty(about) ? null : about;
        await db.SaveChangesAsync();
        return chat.ToUserDto(user);
    }
}
