using GlasFlow.Application.Common;
using GlasFlow.Application.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GlasFlow.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<InventoryOptions>(config.GetSection(InventoryOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<AuditService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<ProductService>();
        services.AddScoped<WarehouseService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<SalesOrderService>();
        services.AddScoped<ProductionService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<InventoryAlertService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ReportService>();
        services.AddScoped<ImportService>();
        services.AddScoped<BusinessDataService>();
        services.AddScoped<AssistantService>();
        return services;
    }
}
