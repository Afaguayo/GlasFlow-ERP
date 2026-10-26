using System.Security.Claims;
using System.Text;
using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Application.Services;
using GlasFlow.Domain.Entities;
using GlasFlow.Infrastructure.Persistence;
using GlasFlow.Infrastructure.Security;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace GlasFlow.Api.Infrastructure;

public class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserName => accessor.HttpContext?.User.Identity?.Name ?? "system";
}

public class JwtOptions
{
    public const string Issuer = "GlasFlow";
    public const string Audience = "GlasFlow.Clients";
    public required SymmetricSecurityKey Key { get; init; }
    public int ExpiryMinutes { get; init; } = 480;

    /// <summary>
    /// Signing key from Auth:JwtKey (env Auth__JwtKey). In Development a random per-process key is used when none
    /// is configured (tokens then expire on restart); outside Development a missing key is a startup error.
    /// </summary>
    public static JwtOptions From(IConfiguration config, IHostEnvironment env)
    {
        var configured = config["Auth:JwtKey"];
        byte[] key;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            key = Encoding.UTF8.GetBytes(configured);
            if (key.Length < 32) throw new InvalidOperationException("Auth:JwtKey must be at least 32 bytes.");
        }
        else if (env.IsDevelopment() || env.IsEnvironment("Testing"))
            key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(48);
        else throw new InvalidOperationException("Auth:JwtKey must be configured outside Development.");
        return new JwtOptions { Key = new SymmetricSecurityKey(key), ExpiryMinutes = config.GetValue("Auth:TokenMinutes", 480) };
    }
}

public class AuthService(GlasFlowDbContext db, JwtOptions jwt, TimeProvider clock)
{
    public async Task<LoginResponse?> LoginAsync(LoginRequest r, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == r.Username && u.Active, ct);
        // Same response for unknown user and wrong password so usernames cannot be enumerated.
        if (user is null || !PasswordHasher.Verify(r.Password, user.PasswordHash)) return null;

        var now = clock.GetUtcNow().UtcDateTime;
        user.LastLoginAt = now;
        db.AuditLogs.Add(new AuditLog { Timestamp = now, UserName = user.Username, Action = "Login", EntityType = nameof(User), EntityId = user.Id.ToString(), Source = "APP" });
        await db.SaveChangesAsync(ct);

        var expires = now.AddMinutes(jwt.ExpiryMinutes);
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = JwtOptions.Issuer,
            Audience = JwtOptions.Audience,
            Expires = expires,
            IssuedAt = now,
            NotBefore = now,
            SigningCredentials = new SigningCredentials(jwt.Key, SecurityAlgorithms.HmacSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("display_name", user.DisplayName)
            ])
        });
        return new LoginResponse(token, expires, user.Username, user.DisplayName, user.Role);
    }
}

/// <summary>Maps business exceptions to RFC 7807 problem responses; never leaks stack traces.</summary>
public class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception ex, CancellationToken ct)
    {
        var (status, title, detail) = ex switch
        {
            NotFoundException => (404, "Not found", ex.Message),
            ConflictException => (409, "Conflict", ex.Message),
            DomainException => (422, "Business rule violation", ex.Message),
            DbUpdateConcurrencyException => (409, "Concurrent update", "The record was changed by another user. Reload and try again."),
            DbUpdateException => (409, "Data conflict", "The change conflicts with existing data (duplicate or referenced record)."),
            BadHttpRequestException b => (b.StatusCode, "Bad request", b.Message),
            _ => (500, "Server error", "An unexpected error occurred.")
        };
        if (status >= 500) logger.LogError(ex, "Unhandled error on {Path}", http.Request.Path);
        else logger.LogInformation("{Status} on {Path}: {Message}", status, http.Request.Path, ex.Message);

        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            Exception = ex,
            ProblemDetails = new ProblemDetails { Status = status, Title = title, Detail = detail }
        });
    }
}

public class DatabaseHealthCheck(GlasFlowDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy($"{db.Database.ProviderName} reachable")
            : HealthCheckResult.Unhealthy("Database unreachable");
}

/// <summary>Scheduled low-stock check. The alert service is idempotent, so repeated runs never duplicate alerts.</summary>
public class LowStockMonitor(IServiceScopeFactory scopes, IConfiguration config, ILogger<LowStockMonitor> logger) : BackgroundService
{
    public static DateTime? LastRun { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = config.GetValue("Automation:InventoryCheckMinutes", 15);
        if (minutes <= 0) { logger.LogInformation("Scheduled inventory check disabled"); return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<InventoryAlertService>().RunFullCheckAsync(stoppingToken);
                LastRun = DateTime.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduled inventory check failed");
            }
        }
    }
}

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        if (!config.GetValue("Database:AutoMigrate", true)) return;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GlasFlowDbContext>();
        if (db.Database.IsSqlServer())
        {
            await db.Database.MigrateAsync();
            if (config.GetValue("Database:InstallSqlObjects", true))
                await scope.ServiceProvider.GetRequiredService<SqlServerObjectsInstaller>()
                    .InstallAsync(Path.Combine(AppContext.BaseDirectory, "database"));
        }
        else await db.Database.EnsureCreatedAsync();

        if (config.GetValue("Database:Seed", true))
            await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(config["Auth:DemoPassword"], env.IsDevelopment());
        var result = await scope.ServiceProvider.GetRequiredService<InventoryAlertService>().RunFullCheckAsync(CancellationToken.None);
        logger.LogInformation("Startup inventory check: {Low} low-stock rows, {Created} new alerts", result.LowStockItems, result.NotificationsCreated);
    }
}
