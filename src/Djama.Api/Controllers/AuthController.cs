using System.Text.RegularExpressions;
using Djama.Api.Data;
using Djama.Api.Domain;
using Djama.Api.Dtos;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Djama.Api.Controllers;

[ApiController, Route("api/auth")]
public class AuthController(DjamaDbContext db, TokenService tokens, ChatService chat) : ControllerBase
{
    private static readonly Regex UsernameRx = new("^[a-z0-9_]{3,32}$");

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest req)
    {
        var username = (req.Username ?? "").Trim().ToLowerInvariant();
        var email = (req.Email ?? "").Trim().ToLowerInvariant();
        var display = (req.DisplayName ?? "").Trim();
        if (!UsernameRx.IsMatch(username)) throw DomainException.Validation("Username: 3-32 characters, a-z, 0-9 and _ only");
        if (display.Length is < 1 or > 64) throw DomainException.Validation("Display name: 1-64 characters");
        if (email.Length > 128 || !email.Contains('@')) throw DomainException.Validation("Invalid e-mail address");
        if ((req.Password ?? "").Length < 6) throw DomainException.Validation("Password: at least 6 characters");
        if (await db.Users.AnyAsync(u => u.Username == username || u.Email == email))
            throw DomainException.Conflict("Username or e-mail is already taken");

        var user = new AppUser
        {
            Username = username, Email = email, DisplayName = display,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return Build(user);
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest req)
    {
        var id = (req.UsernameOrEmail ?? "").Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == id || u.Email == id);
        if (user == null || !BCrypt.Net.BCrypt.Verify(req.Password ?? "", user.PasswordHash))
            throw new DomainException(401, "INVALID_CREDENTIALS", "Invalid username or password");
        return Build(user);
    }

    [Authorize, HttpGet("me")]
    public async Task<ActionResult<UserDto>> Me()
    {
        var user = await db.Users.FindAsync(User.GetUserId()) ?? throw DomainException.NotFound("User not found");
        return chat.ToUserDto(user);
    }

    private AuthResponse Build(AppUser user)
    {
        var (token, expires) = tokens.Create(user);
        return new AuthResponse(token, expires, chat.ToUserDto(user));
    }
}
