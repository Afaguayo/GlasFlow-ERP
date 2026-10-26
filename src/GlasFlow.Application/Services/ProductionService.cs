using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GlasFlow.Application.Services;

/// <summary>
/// Production workflow: Planned -> InProgress -> (register completed units, each one received into
/// finished-goods stock) -> Completed. Cancelled is allowed before completion.
/// </summary>
public class ProductionService(
    IGlasFlowDbContext db, AuditService audit, TimeProvider clock, InventoryService inventory,
    InventoryAlertService alerts, NotificationService notifications)
{
    public Task<PagedResult<ProductionOrderDto>> ListAsync(ProductionQuery q, CancellationToken ct)
    {
        var query = Orders().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Status) && Enum.TryParse<ProductionStatus>(q.Status, true, out var status))
            query = query.Where(p => p.Status == status);
        if (!string.IsNullOrWhiteSpace(q.Search))
            query = query.Where(p => p.ProductionNumber.Contains(q.Search) || p.Product!.ProductName.Contains(q.Search));
        return query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .ToPagedAsync(new PageQuery(q.Page, q.PageSize), p => p.ToDto(), ct);
    }

    public async Task<ProductionOrderDto> GetAsync(int id, CancellationToken ct) =>
        (await Orders().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Production order", id)).ToDto();

    public async Task<ProductionOrderDto> CreateAsync(CreateProductionOrderRequest r, CancellationToken ct)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == r.ProductId, ct) ?? throw new NotFoundException("Product", r.ProductId);
        if (!product.Active) throw new DomainException($"Product {product.ProductCode} is inactive.");
        if (product.Category == ProductCategories.Accessories)
            throw new DomainException("Accessories are purchased, not produced; create a production order for a glass product.");
        if (!await db.Warehouses.AnyAsync(w => w.Id == r.WarehouseId && w.Active, ct)) throw new NotFoundException("Warehouse", r.WarehouseId);
        if (r.QuantityPlanned <= 0) throw new DomainException("Planned quantity must be greater than zero.");

        var now = Now;
        var order = new ProductionOrder
        {
            ProductionNumber = await NextNumberAsync(now, ct),
            ProductId = product.Id,
            WarehouseId = r.WarehouseId,
            QuantityPlanned = r.QuantityPlanned,
            DueDate = r.DueDate,
            Notes = r.Notes,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProductionOrders.Add(order);
        await db.SaveChangesAsync(ct);
        audit.Record("ProductionCreated", nameof(ProductionOrder), order.Id, $"{order.ProductionNumber} {product.ProductCode} x {r.QuantityPlanned}");
        await db.SaveChangesAsync(ct);
        return await GetAsync(order.Id, ct);
    }

    public async Task<ProductionOrderDto> StartAsync(int id, CancellationToken ct)
    {
        var order = await LoadAsync(id, ct);
        order.Start(Now);
        order.UpdatedAt = Now;
        audit.Record("ProductionStarted", nameof(ProductionOrder), id, order.ProductionNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Registers finished units: they enter the destination warehouse immediately.</summary>
    public async Task<ProductionOrderDto> RegisterAsync(int id, decimal quantity, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LoadAsync(id, ct);
        order.RegisterCompleted(quantity);
        order.UpdatedAt = Now;
        var stock = await inventory.GetOrCreateStockAsync(order.ProductId, order.WarehouseId, ct);
        stock.Receive(quantity);
        stock.UpdatedAt = Now;
        inventory.AddMovement(stock, MovementType.ProductionReceipt, quantity, order.ProductionNumber);
        audit.Record("ProductionRegistered", nameof(ProductionOrder), id, $"{order.ProductionNumber} +{quantity}");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await alerts.EvaluateAsync(order.ProductId, order.WarehouseId, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Completes the order. If quantity is given it is registered first, in the same transaction.</summary>
    public async Task<ProductionOrderDto> CompleteAsync(int id, decimal? finalQuantity, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LoadAsync(id, ct);
        if (finalQuantity is > 0)
        {
            order.RegisterCompleted(finalQuantity.Value);
            var stock = await inventory.GetOrCreateStockAsync(order.ProductId, order.WarehouseId, ct);
            stock.Receive(finalQuantity.Value);
            stock.UpdatedAt = Now;
            inventory.AddMovement(stock, MovementType.ProductionReceipt, finalQuantity.Value, order.ProductionNumber);
        }
        order.Complete(Now);
        order.UpdatedAt = Now;
        audit.Record("ProductionCompleted", nameof(ProductionOrder), id,
            $"{order.ProductionNumber} completed {order.QuantityCompleted}/{order.QuantityPlanned}");
        notifications.Add(NotificationType.ProductionCompleted, NotificationSeverity.Info,
            $"Producción {order.ProductionNumber} terminada",
            $"{order.Product!.ProductName}: {order.QuantityCompleted} {order.Product.Unit} ingresados a {order.Warehouse!.Code}.",
            order.ProductId, order.WarehouseId);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await alerts.EvaluateAsync(order.ProductId, order.WarehouseId, ct);
        return await GetAsync(id, ct);
    }

    public async Task<ProductionOrderDto> CancelAsync(int id, CancellationToken ct)
    {
        var order = await LoadAsync(id, ct);
        order.Cancel();
        order.UpdatedAt = Now;
        audit.Record("ProductionCancelled", nameof(ProductionOrder), id, order.ProductionNumber);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    private IQueryable<ProductionOrder> Orders() => db.ProductionOrders.Include(p => p.Product).Include(p => p.Warehouse);

    private async Task<ProductionOrder> LoadAsync(int id, CancellationToken ct) =>
        await Orders().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Production order", id);

    private async Task<string> NextNumberAsync(DateTime now, CancellationToken ct)
    {
        var prefix = $"PO-{now:yyyy}-";
        var last = await db.ProductionOrders.Where(o => o.ProductionNumber.StartsWith(prefix))
            .OrderByDescending(o => o.ProductionNumber).Select(o => o.ProductionNumber).FirstOrDefaultAsync(ct);
        var next = last is null ? 1 : int.Parse(last[prefix.Length..]) + 1;
        return $"{prefix}{next:0000}";
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}
