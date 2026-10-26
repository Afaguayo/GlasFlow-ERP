using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace GlasFlow.Application.Common;

/// <summary>Persistence boundary used by application services (implemented by the EF Core DbContext).</summary>
public interface IGlasFlowDbContext
{
    DbSet<Customer> Customers { get; }
    DbSet<Supplier> Suppliers { get; }
    DbSet<Product> Products { get; }
    DbSet<Warehouse> Warehouses { get; }
    DbSet<InventoryItem> Inventory { get; }
    DbSet<SalesOrder> SalesOrders { get; }
    DbSet<SalesOrderDetail> SalesOrderDetails { get; }
    DbSet<ProductionOrder> ProductionOrders { get; }
    DbSet<InventoryMovement> InventoryMovements { get; }
    DbSet<User> Users { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Notification> Notifications { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface ICurrentUser
{
    string UserName { get; }
}

public class InventoryOptions
{
    public const string Section = "Inventory";
    /// <summary>When false (default) no operation may leave free stock below zero.</summary>
    public bool AllowNegativeStock { get; set; }
}

public class NotFoundException(string entity, object key) : Exception($"{entity} '{key}' was not found.");

/// <summary>Request is well-formed but conflicts with existing data (duplicate code, concurrent update...).</summary>
public class ConflictException(string message) : Exception(message);

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

public record PageQuery(int Page = 1, int PageSize = 25, string? Search = null)
{
    public int SafePage => Math.Max(1, Page);
    public int SafePageSize => Math.Clamp(PageSize, 1, 200);
}

public static class QueryExtensions
{
    public static async Task<PagedResult<TOut>> ToPagedAsync<TIn, TOut>(
        this IQueryable<TIn> query, PageQuery page, Func<TIn, TOut> map, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip((page.SafePage - 1) * page.SafePageSize).Take(page.SafePageSize).ToListAsync(ct);
        return new PagedResult<TOut>(items.Select(map).ToList(), page.SafePage, page.SafePageSize, total);
    }
}

/// <summary>Writes application-level audit records. Saved together with the caller's unit of work.</summary>
public class AuditService(IGlasFlowDbContext db, ICurrentUser user, TimeProvider clock)
{
    public void Record(string action, string entityType, object? entityId, string? details = null)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = clock.GetUtcNow().UtcDateTime,
            UserName = user.UserName,
            Action = action,
            EntityType = entityType,
            EntityId = entityId?.ToString(),
            Details = details,
            Source = "APP"
        });
    }
}
