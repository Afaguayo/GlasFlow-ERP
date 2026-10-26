using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GlasFlow.Application.Services;

/// <summary>
/// Sales workflow: Draft -> Confirmed (stock reserved) -> Shipped (stock deducted), or Cancelled (reservation released).
/// Every state change runs in one database transaction together with its stock changes, movements and audit rows.
/// </summary>
public class SalesOrderService(
    IGlasFlowDbContext db, AuditService audit, TimeProvider clock, InventoryService inventory,
    InventoryAlertService alerts, NotificationService notifications, IOptions<InventoryOptions> options)
{
    public Task<PagedResult<SalesOrderSummaryDto>> ListAsync(SalesOrderQuery q, CancellationToken ct)
    {
        var query = Orders().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Status) && Enum.TryParse<SalesOrderStatus>(q.Status, true, out var status))
            query = query.Where(o => o.Status == status);
        if (q.CustomerId is not null) query = query.Where(o => o.CustomerId == q.CustomerId);
        if (q.From is not null) query = query.Where(o => o.OrderDate >= q.From);
        if (q.To is not null) query = query.Where(o => o.OrderDate < q.To.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(q.Search))
            query = query.Where(o => o.OrderNumber.Contains(q.Search) || o.Customer!.Name.Contains(q.Search));
        return query.OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Id)
            .ToPagedAsync(new PageQuery(q.Page, q.PageSize), o => o.ToSummary(), ct);
    }

    public async Task<SalesOrderDto> GetAsync(int id, CancellationToken ct) =>
        (await Orders().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Sales order", id)).ToDto();

    public async Task<SalesOrderDto> CreateAsync(CreateSalesOrderRequest r, CancellationToken ct)
    {
        if (r.Lines.Count == 0) throw new DomainException("An order needs at least one line.");
        if (r.Lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
            throw new DomainException("Each product may appear only once per order; combine the quantities.");
        if (!string.IsNullOrWhiteSpace(r.ExternalReference) &&
            await db.SalesOrders.AnyAsync(o => o.ExternalReference == r.ExternalReference, ct))
            throw new ConflictException($"An order with reference '{r.ExternalReference}' already exists (duplicate submission).");

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == r.CustomerId, ct) ?? throw new NotFoundException("Customer", r.CustomerId);
        if (!customer.Active) throw new DomainException($"Customer {customer.CustomerCode} is inactive.");
        var warehouse = await db.Warehouses.FirstOrDefaultAsync(w => w.Id == r.WarehouseId, ct) ?? throw new NotFoundException("Warehouse", r.WarehouseId);
        if (!warehouse.Active) throw new DomainException($"Warehouse {warehouse.Code} is inactive.");

        var ids = r.Lines.Select(l => l.ProductId).ToList();
        var products = await db.Products.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var now = Now;
        var order = new SalesOrder
        {
            OrderNumber = await NextNumberAsync(now, ct),
            CustomerId = customer.Id,
            WarehouseId = warehouse.Id,
            OrderDate = now,
            Notes = r.Notes,
            ExternalReference = string.IsNullOrWhiteSpace(r.ExternalReference) ? null : r.ExternalReference.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        foreach (var line in r.Lines)
        {
            if (!products.TryGetValue(line.ProductId, out var product)) throw new NotFoundException("Product", line.ProductId);
            order.AddLine(product, line.Quantity, line.UnitPrice);
        }
        order.RecalculateTotals(customer.TaxZone);

        db.SalesOrders.Add(order);
        await db.SaveChangesAsync(ct);
        audit.Record("SalesOrderCreated", nameof(SalesOrder), order.Id, $"{order.OrderNumber} total {order.Total:0.00} MXN");
        await db.SaveChangesAsync(ct);
        return await GetAsync(order.Id, ct);
    }

    /// <summary>Confirms a draft order and reserves its stock. Fails atomically if any line lacks free stock.</summary>
    public async Task<SalesOrderDto> ConfirmAsync(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LoadForUpdateAsync(id, ct);
        order.MarkConfirmed(Now);

        var shortages = new List<string>();
        foreach (var line in order.Details)
        {
            var stock = await inventory.GetOrCreateStockAsync(line.ProductId, order.WarehouseId, ct);
            if (!options.Value.AllowNegativeStock && stock.QuantityFree < line.Quantity)
            {
                shortages.Add($"{line.Product!.ProductCode} (requested {line.Quantity}, free {stock.QuantityFree})");
                continue;
            }
            stock.Reserve(line.Quantity, options.Value.AllowNegativeStock);
            stock.UpdatedAt = Now;
            inventory.AddMovement(stock, MovementType.Reservation, line.Quantity, order.OrderNumber, "Reserved for order");
        }
        if (shortages.Count > 0)
            throw new DomainException($"Insufficient stock in warehouse for: {string.Join("; ", shortages)}.");

        order.UpdatedAt = Now;
        audit.Record("SalesOrderConfirmed", nameof(SalesOrder), order.Id, order.OrderNumber);
        notifications.Add(NotificationType.OrderConfirmed, NotificationSeverity.Info, $"Pedido {order.OrderNumber} confirmado",
            $"{order.Customer!.Name}: {order.Total:N2} MXN, inventario reservado.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await alerts.EvaluateProductsAsync(order.Details.Select(d => d.ProductId), order.WarehouseId, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Ships a confirmed order: reserved stock leaves the warehouse.</summary>
    public async Task<SalesOrderDto> ShipAsync(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LoadForUpdateAsync(id, ct);
        order.MarkShipped(Now);
        foreach (var line in order.Details)
        {
            var stock = await inventory.GetOrCreateStockAsync(line.ProductId, order.WarehouseId, ct);
            stock.ShipReserved(line.Quantity);
            stock.UpdatedAt = Now;
            inventory.AddMovement(stock, MovementType.SaleShipment, -line.Quantity, order.OrderNumber);
        }
        order.UpdatedAt = Now;
        audit.Record("SalesOrderShipped", nameof(SalesOrder), order.Id, order.OrderNumber);
        notifications.Add(NotificationType.OrderShipped, NotificationSeverity.Info, $"Pedido {order.OrderNumber} embarcado",
            $"{order.Details.Count} partidas enviadas a {order.Customer!.Name}.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await alerts.EvaluateProductsAsync(order.Details.Select(d => d.ProductId), order.WarehouseId, ct);
        return await GetAsync(id, ct);
    }

    public async Task<SalesOrderDto> CancelAsync(int id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = await LoadForUpdateAsync(id, ct);
        var wasConfirmed = order.Status == SalesOrderStatus.Confirmed;
        order.MarkCancelled();
        if (wasConfirmed)
        {
            foreach (var line in order.Details)
            {
                var stock = await inventory.GetOrCreateStockAsync(line.ProductId, order.WarehouseId, ct);
                stock.Release(line.Quantity);
                stock.UpdatedAt = Now;
                inventory.AddMovement(stock, MovementType.ReservationRelease, -line.Quantity, order.OrderNumber, "Order cancelled");
            }
        }
        order.UpdatedAt = Now;
        audit.Record("SalesOrderCancelled", nameof(SalesOrder), order.Id, order.OrderNumber);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (wasConfirmed) await alerts.EvaluateProductsAsync(order.Details.Select(d => d.ProductId), order.WarehouseId, ct);
        return await GetAsync(id, ct);
    }

    private IQueryable<SalesOrder> Orders() => db.SalesOrders
        .Include(o => o.Customer).Include(o => o.Warehouse).Include(o => o.Details).ThenInclude(d => d.Product);

    private async Task<SalesOrder> LoadForUpdateAsync(int id, CancellationToken ct) =>
        await Orders().FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw new NotFoundException("Sales order", id);

    /// <summary>Order numbers follow SO-YYYY-NNNN and restart each year.</summary>
    private async Task<string> NextNumberAsync(DateTime now, CancellationToken ct)
    {
        var prefix = $"SO-{now:yyyy}-";
        var last = await db.SalesOrders.Where(o => o.OrderNumber.StartsWith(prefix))
            .OrderByDescending(o => o.OrderNumber).Select(o => o.OrderNumber).FirstOrDefaultAsync(ct);
        var next = last is null ? 1 : int.Parse(last[prefix.Length..]) + 1;
        return $"{prefix}{next:0000}";
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}
