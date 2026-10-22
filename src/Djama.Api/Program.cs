using System.Text;
using Djama.Api.Data;
using Djama.Api.Hubs;
using Djama.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------- services ----------
builder.Services.AddDbContext<DjamaDbContext>(o => o.UseNpgsql(cfg.GetConnectionString("Default")));
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Djama API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http, Scheme = "bearer",
        BearerFormat = "JWT", In = ParameterLocation.Header, Description = "Paste the JWT returned by /api/auth/login"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
    });
});

builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<IRealtimeNotifier, SignalRNotifier>();
builder.Services.AddSingleton<CallTracker>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddScoped<CallService>();

var jwtKey = cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key is not configured");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidIssuer = cfg["Jwt:Issuer"],
        ValidateAudience = true, ValidAudience = cfg["Jwt:Audience"],
        ValidateIssuerSigningKey = true, IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(1)
    };
    // Browsers cannot set headers on WebSockets: SignalR sends the JWT as ?access_token=...
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && (ctx.HttpContext.Request.Path.StartsWithSegments("/hubs") || ctx.HttpContext.Request.Path.StartsWithSegments("/api/attachments")))
                ctx.Token = token;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

var app = builder.Build();

// ---------- create / migrate the database (with retries while PostgreSQL starts) ----------
for (var attempt = 1; attempt <= 10; attempt++)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DjamaDbContext>();
        if (cfg.GetValue<bool>("Database:UseMigrations")) db.Database.Migrate();   // after P2 creates migrations
        else
        {
            db.Database.EnsureCreated();     // first start: creates every table
            DbUpgrade.Apply(db);             // v1 -> v2: adds the new columns to an existing database
        }
        break;
    }
    catch (Exception ex) when (attempt < 10)
    {
        app.Logger.LogWarning(ex, "Database not ready (attempt {Attempt}/10), retrying...", attempt);
        Thread.Sleep(2000);
    }
}

// ---------- pipeline ----------
// DomainException -> { "code": "...", "error": "..." } with the right HTTP status
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (DomainException ex) when (!ctx.Response.HasStarted)
    {
        ctx.Response.StatusCode = ex.Status;
        await ctx.Response.WriteAsJsonAsync(new { code = ex.Code, error = ex.Message });
    }
});

app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();
