using GlasFlow.Application.Common;
using GlasFlow.Application.Services;
using GlasFlow.Infrastructure.Ai;
using GlasFlow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlasFlow.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Database:Provider selects SqlServer (default, uses ConnectionStrings:GlasFlow) or Sqlite
    /// (ConnectionStrings:GlasFlowSqlite) for machines without SQL Server and for automated tests.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var provider = DatabaseProvider(config);
        services.AddDbContext<GlasFlowDbContext>((sp, o) =>
        {
            if (provider == "Sqlite")
                o.UseSqlite(config.GetConnectionString("GlasFlowSqlite") ?? "Data Source=glasflow.db");
            else
                o.UseSqlServer(config.GetConnectionString("GlasFlow")
                    ?? throw new InvalidOperationException("ConnectionStrings:GlasFlow is not configured."),
                    // No retrying execution strategy: workflows use explicit transactions, which it does not allow.
                    sql => sql.CommandTimeout(60))
                 .AddInterceptors(new SessionContextInterceptor(sp.GetService<ICurrentUser>() ?? new SystemUser()));
        });
        services.AddScoped<IGlasFlowDbContext>(sp => sp.GetRequiredService<GlasFlowDbContext>());
        services.AddScoped<DataSeeder>();
        services.AddScoped<SqlServerObjectsInstaller>();

        services.Configure<AiOptions>(o =>
        {
            config.GetSection(AiOptions.Section).Bind(o);
            o.ApiKey ??= Environment.GetEnvironmentVariable("GLASFLOW_AI_API_KEY");
        });
        services.AddHttpClient<AnthropicProvider>(c => c.Timeout = TimeSpan.FromSeconds(config.GetValue("Ai:TimeoutSeconds", 30)));
        services.AddHttpClient<OpenAiCompatibleProvider>(c => c.Timeout = TimeSpan.FromSeconds(config.GetValue("Ai:TimeoutSeconds", 30)));
        services.AddScoped<IAiProvider>(sp => sp.GetRequiredService<AnthropicProvider>());
        services.AddScoped<IAiProvider>(sp => sp.GetRequiredService<OpenAiCompatibleProvider>());
        return services;
    }

    private sealed class SystemUser : ICurrentUser
    {
        public string UserName => "system";
    }

    public static string DatabaseProvider(IConfiguration config) =>
        string.Equals(config["Database:Provider"], "Sqlite", StringComparison.OrdinalIgnoreCase) ? "Sqlite" : "SqlServer";
}
