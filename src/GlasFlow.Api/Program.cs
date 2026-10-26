using System.Text.Json.Serialization;
using Microsoft.AspNetCore.RateLimiting;
using GlasFlow.Api.Infrastructure;
using GlasFlow.Application;
using GlasFlow.Application.Common;
using GlasFlow.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddApplication(config);
builder.Services.AddInfrastructure(config);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<AuthService>();

// ---- Authentication: JWT bearer, roles Admin / Operator / Viewer ----
var jwt = JwtOptions.From(config, builder.Environment);
builder.Services.AddSingleton(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = JwtOptions.Issuer,
        ValidAudience = JwtOptions.Audience,
        IssuerSigningKey = jwt.Key,
        NameClaimType = System.Security.Claims.ClaimTypes.Name,
        RoleClaimType = System.Security.Claims.ClaimTypes.Role,
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});
// Every endpoint requires a signed-in user unless it opts out with [AllowAnonymous].
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddFixedWindowLimiter("login", l => { l.PermitLimit = config.GetValue("RateLimits:LoginPerMinute", 10); l.Window = TimeSpan.FromMinutes(1); });
    o.AddFixedWindowLimiter("assistant", l => { l.PermitLimit = config.GetValue("RateLimits:AssistantPerMinute", 20); l.Window = TimeSpan.FromMinutes(1); });
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");
builder.Services.AddHostedService<LowStockMonitor>();
builder.Services.AddOpenApi(o => o.AddDocumentTransformer((doc, _, _) =>
{
    doc.Info.Title = "GlasFlow ERP API";
    doc.Info.Description = "REST API for the GlasFlow ERP demo: catalog, inventory, sales, production, reports, integration and AI assistant. " +
                           "Authenticate with POST /api/auth/login and send 'Authorization: Bearer <token>'.";
    return Task.CompletedTask;
}));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseDefaultFiles();
app.UseStaticFiles();
// Swagger UI is static documentation; it must run before the "authenticated by default" authorization policy.
app.UseSwaggerUI(o =>
{
    o.SwaggerEndpoint("/openapi/v1.json", "GlasFlow ERP API v1");
    o.RoutePrefix = "swagger";
    o.DocumentTitle = "GlasFlow ERP API";
});
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapOpenApi().AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (ctx, report) =>
    {
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new { name = e.Key, status = e.Value.Status.ToString(), e.Value.Description })
        });
    }
}).AllowAnonymous();
app.MapControllers();

await DatabaseInitializer.InitializeAsync(app.Services, config, app.Environment, app.Logger);
app.Run();

public partial class Program;
