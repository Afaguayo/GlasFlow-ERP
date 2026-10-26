using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GlasFlow.Application.Services;

public class NotificationService(IGlasFlowDbContext db, TimeProvider clock)
{
    /// <summary>Queues a notification; it is saved with the caller's unit of work.</summary>
    public void Add(NotificationType type, NotificationSeverity severity, string title, string message,
        int? productId = null, int? warehouseId = null, string? dedupKey = null) =>
        db.Notifications.Add(new Notification
        {
            Type = type, Severity = severity, Title = title, Message = message, ProductId = productId,
            WarehouseId = warehouseId, DedupKey = dedupKey, CreatedAt = clock.GetUtcNow().UtcDateTime
        });

    public async Task<IReadOnlyList<NotificationDto>> ListAsync(bool unreadOnly, bool includeResolved, int take, CancellationToken ct)
    {
        var q = db.Notifications.AsNoTracking();
        if (unreadOnly) q = q.Where(n => !n.IsRead);
        if (!includeResolved) q = q.Where(n => !n.IsResolved);
        var items = await q.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return items.Select(n => n.ToDto()).ToList();
    }

    public async Task MarkReadAsync(int id, CancellationToken ct)
    {
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Notification", id);
        n.IsRead = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> MarkAllReadAsync(CancellationToken ct)
    {
        var unread = await db.Notifications.Where(n => !n.IsRead).ToListAsync(ct);
        unread.ForEach(n => n.IsRead = true);
        await db.SaveChangesAsync(ct);
        return unread.Count;
    }
}

/// <summary>
/// Low-stock automation. Deterministic and idempotent: for each stock row it either ensures exactly one open
/// LowStock notification (DedupKey LOWSTOCK:{product}:{warehouse}) or resolves the open one when stock recovers.
/// Running it twice in a row never creates duplicates.
/// </summary>
public class InventoryAlertService(IGlasFlowDbContext db, AuditService audit, TimeProvider clock, ILogger<InventoryAlertService> logger)
{
    public static string KeyFor(int productId, int warehouseId) => $"LOWSTOCK:{productId}:{warehouseId}";

    public Task<InventoryCheckResult> RunFullCheckAsync(CancellationToken ct) => CheckAsync(db.Inventory, ct, recordAudit: true);

    public Task<InventoryCheckResult> EvaluateAsync(int productId, int? warehouseId, CancellationToken ct) =>
        CheckAsync(db.Inventory.Where(i => i.ProductId == productId && (warehouseId == null || i.WarehouseId == warehouseId)), ct);

    public Task<InventoryCheckResult> EvaluateProductsAsync(IEnumerable<int> productIds, int warehouseId, CancellationToken ct)
    {
        var ids = productIds.Distinct().ToList();
        return CheckAsync(db.Inventory.Where(i => ids.Contains(i.ProductId) && i.WarehouseId == warehouseId), ct);
    }

    private async Task<InventoryCheckResult> CheckAsync(IQueryable<InventoryItem> scope, CancellationToken ct, bool recordAudit = false)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var rows = await scope.Include(i => i.Product).Include(i => i.Warehouse).Where(i => i.Product!.Active).ToListAsync(ct);
        var open = await db.Notifications.Where(n => n.Type == NotificationType.LowStock && !n.IsResolved && n.DedupKey != null)
            .ToDictionaryAsync(n => n.DedupKey!, ct);

        int low = 0, created = 0, resolved = 0;
        foreach (var row in rows)
        {
            var key = KeyFor(row.ProductId, row.WarehouseId);
            var isLow = row.IsLowStock(row.Product!.MinimumStock);
            if (isLow)
            {
                low++;
                if (open.ContainsKey(key)) continue;
                db.Notifications.Add(new Notification
                {
                    Type = NotificationType.LowStock,
                    Severity = row.QuantityFree <= 0 ? NotificationSeverity.Critical : NotificationSeverity.Warning,
                    Title = $"Inventario bajo: {row.Product.ProductCode}",
                    Message = $"{row.Product.ProductName} en {row.Warehouse!.Code}: {row.QuantityFree:0.##} {row.Product.Unit} libres " +
                              $"(mínimo {row.Product.MinimumStock:0.##}). Considere una orden de producción o compra.",
                    ProductId = row.ProductId,
                    WarehouseId = row.WarehouseId,
                    DedupKey = key,
                    CreatedAt = now
                });
                created++;
            }
            else if (open.TryGetValue(key, out var existing))
            {
                existing.IsResolved = true;
                existing.ResolvedAt = now;
                resolved++;
            }
        }
        if (recordAudit) audit.Record("InventoryCheck", "Inventory", null, $"checked {rows.Count}, low {low}, created {created}, resolved {resolved}");
        if (created + resolved > 0 || recordAudit) await db.SaveChangesAsync(ct);
        if (created + resolved > 0) logger.LogInformation("Low-stock check: {Created} alerts created, {Resolved} resolved", created, resolved);
        return new InventoryCheckResult(now, rows.Count, low, created, resolved);
    }
}
