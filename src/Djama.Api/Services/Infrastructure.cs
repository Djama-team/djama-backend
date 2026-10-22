using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Djama.Api.Domain;
using Djama.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;

namespace Djama.Api.Services;

/// <summary>Business error -> HTTP status + stable error code (the frontend translates the code).</summary>
public class DomainException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public static DomainException NotFound(string m = "Not found") => new(404, "NOT_FOUND", m);
    public static DomainException Forbidden(string m = "Forbidden") => new(403, "FORBIDDEN", m);
    public static DomainException Validation(string m) => new(400, "VALIDATION", m);
    public static DomainException Conflict(string m) => new(409, "CONFLICT", m);
    public static DomainException Blocked(string m = "User is blocked") => new(403, "BLOCKED", m);
}

public static class ClaimsExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal p) =>
        Guid.Parse(p.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? throw new UnauthorizedAccessException());
}

/// <summary>Who is connected to SignalR (a user may have several tabs/devices).</summary>
public class PresenceTracker
{
    private readonly Dictionary<Guid, int> _counts = new();
    private readonly object _lock = new();

    /// <returns>true if this is the user's first connection (they just came online)</returns>
    public bool Connected(Guid id)
    {
        lock (_lock) { _counts[id] = _counts.GetValueOrDefault(id) + 1; return _counts[id] == 1; }
    }

    /// <returns>true if this was the user's last connection (they just went offline)</returns>
    public bool Disconnected(Guid id)
    {
        lock (_lock)
        {
            if (!_counts.TryGetValue(id, out var n)) return false;
            if (n <= 1) { _counts.Remove(id); return true; }
            _counts[id] = n - 1;
            return false;
        }
    }

    public bool IsOnline(Guid id) { lock (_lock) return _counts.ContainsKey(id); }
}

/// <summary>Abstraction over SignalR so ChatService can be unit-tested.</summary>
public interface IRealtimeNotifier
{
    Task SendToUsersAsync(IEnumerable<Guid> userIds, string method, object payload);
}

public class SignalRNotifier(IHubContext<ChatHub> hub) : IRealtimeNotifier
{
    public Task SendToUsersAsync(IEnumerable<Guid> userIds, string method, object payload)
    {
        var ids = userIds.Select(i => i.ToString()).Distinct().ToList();
        return ids.Count == 0 ? Task.CompletedTask : hub.Clients.Users(ids).SendAsync(method, payload);
    }
}

public class TokenService(IConfiguration cfg)
{
    public (string Token, DateTime ExpiresAt) Create(AppUser u)
    {
        var expires = DateTime.UtcNow.AddHours(cfg.GetValue("Jwt:ExpireHours", 24));
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),   // SignalR uses this claim as UserIdentifier
            new Claim(ClaimTypes.Name, u.Username)
        };
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(cfg["Jwt:Key"]!)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(cfg["Jwt:Issuer"], cfg["Jwt:Audience"], claims, expires: expires, signingCredentials: creds);
        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
