using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GlasFlow.Application.Services;

public class InventoryService(
    IGlasFlowDbContext db, AuditService audit, ICurrentUser user, TimeProvider clock,
    IOptions<InventoryOptions> options, InventoryAlertService alerts)
{
    private bool AllowNegative => options.Value.AllowNegativeStock;

    public Task<PagedResult<StockDto>> ListStockAsync(StockQuery q, CancellationToken ct)
    {
        var query = db.Inventory.AsNoTracking().Include(i => i.Product).Include(i => i.Warehouse)
            .Where(i => i.Product!.Active);
        if (q.WarehouseId is not null) query = query.Where(i => i.WarehouseId == q.WarehouseId);
        if (!string.IsNullOrWhiteSpace(q.Category)) query = query.Where(i => i.Product!.Category == q.Category);
        if (q.LowStockOnly) query = query.Where(i => i.Product!.MinimumStock > 0 && i.QuantityAvailable - i.QuantityReserved <= i.Product.MinimumStock);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(i => i.Product!.ProductName.Contains(s) || i.Product.ProductCode.Contains(s));
        }
        return query.OrderBy(i => i.Product!.ProductCode).ThenBy(i => i.Warehouse!.Code)
            .ToPagedAsync(new PageQuery(q.Page, q.PageSize), i => i.ToDto(), ct);
    }

    public Task<PagedResult<MovementDto>> ListMovementsAsync(MovementQuery q, CancellationToken ct)
    {
        var query = db.InventoryMovements.AsNoTracking().Include(m => m.Product).Include(m => m.Warehouse).AsQueryable();
        if (q.ProductId is not null) query = query.Where(m => m.ProductId == q.ProductId);
        if (q.WarehouseId is not null) query = query.Where(m => m.WarehouseId == q.WarehouseId);
        if (!string.IsNullOrWhiteSpace(q.MovementType) && Enum.TryParse<MovementType>(q.MovementType, true, out var type))
            query = query.Where(m => m.MovementType == type);
        if (q.From is not null) query = query.Where(m => m.Timestamp >= q.From);
        if (q.To is not null) query = query.Where(m => m.Timestamp < q.To.Value.Date.AddDays(1));
        return query.OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id)
            .ToPagedAsync(new PageQuery(q.Page, q.PageSize), m => m.ToDto(), ct);
    }

    public async Task<StockDto> AdjustAsync(StockAdjustmentRequest r, CancellationToken ct)
    {
        if (r.Quantity == 0) throw new DomainException("Adjustment quantity cannot be zero.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var item = await GetOrCreateStockAsync(r.ProductId, r.WarehouseId, ct);
        item.Adjust(r.Quantity, AllowNegative);
        item.UpdatedAt = Now;
        AddMovement(item, MovementType.Adjustment, r.Quantity, $"ADJ-{Now:yyyyMMddHHmmss}", r.Reason);
        audit.Record("StockAdjusted", "Inventory", item.Id, $"Product {r.ProductId} WH {r.WarehouseId} delta {r.Quantity}: {r.Reason}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await alerts.EvaluateAsync(item.ProductId, item.WarehouseId, ct);
        return (await db.Inventory.AsNoTracking().Include(i => i.Product).Include(i => i.Warehouse).FirstAsync(i => i.Id == item.Id, ct)).ToDto();
    }

    public async Task TransferAsync(StockTransferRequest r, CancellationToken ct)
    {
        if (r.FromWarehouseId == r.ToWarehouseId) throw new DomainException("Source and destination warehouses must differ.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var from = await GetOrCreateStockAsync(r.ProductId, r.FromWarehouseId, ct);
        var to = await GetOrCreateStockAsync(r.ProductId, r.ToWarehouseId, ct);
        if (!AllowNegative && from.QuantityFree < r.Quantity)
            throw new DomainException($"Insufficient free stock in source warehouse: requested {r.Quantity}, free {from.QuantityFree}.");
        from.Adjust(-r.Quantity, AllowNegative);
        to.Receive(r.Quantity);
        from.UpdatedAt = to.UpdatedAt = Now;
        var reference = $"TRF-{Now:yyyyMMddHHmmss}";
        AddMovement(from, MovementType.TransferOut, -r.Quantity, reference, r.Notes);
        AddMovement(to, MovementType.TransferIn, r.Quantity, reference, r.Notes);
        audit.Record("StockTransferred", "Inventory", reference, $"Product {r.ProductId}: {r.Quantity} WH {r.FromWarehouseId} -> {r.ToWarehouseId}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await alerts.EvaluateAsync(r.ProductId, null, ct);
    }

    internal async Task<InventoryItem> GetOrCreateStockAsync(int productId, int warehouseId, CancellationToken ct)
    {
        var item = await db.Inventory.FirstOrDefaultAsync(i => i.ProductId == productId && i.WarehouseId == warehouseId, ct);
        if (item is not null) return item;
        if (!await db.Products.AnyAsync(p => p.Id == productId, ct)) throw new NotFoundException("Product", productId);
        if (!await db.Warehouses.AnyAsync(w => w.Id == warehouseId, ct)) throw new NotFoundException("Warehouse", warehouseId);
        item = new InventoryItem { ProductId = productId, WarehouseId = warehouseId, UpdatedAt = Now };
        db.Inventory.Add(item);
        return item;
    }

    internal void AddMovement(InventoryItem item, MovementType type, decimal quantity, string reference, string? notes = null) =>
        db.InventoryMovements.Add(new InventoryMovement
        {
            ProductId = item.ProductId,
            WarehouseId = item.WarehouseId,
            MovementType = type,
            Quantity = quantity,
            Reference = reference,
            Notes = notes,
            Timestamp = Now,
            UserName = user.UserName
        });

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}
