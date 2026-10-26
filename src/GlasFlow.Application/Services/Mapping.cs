using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;

namespace GlasFlow.Application.Services;

internal static class Mapping
{
    public static CustomerDto ToDto(this Customer c) => new(c.Id, c.CustomerCode, c.ExternalId, c.Name, c.Rfc, c.ContactName,
        c.Email, c.Phone, c.Address, c.City, c.State, c.PostalCode, c.TaxZone.ToString(), c.CreditLimit, c.Active, c.CreatedAt, c.UpdatedAt);

    public static StockDto ToDto(this InventoryItem i) => new(i.Id, i.ProductId, i.Product!.ProductCode, i.Product.ProductName,
        i.Product.Category, i.Product.Unit, i.WarehouseId, i.Warehouse!.Code, i.Warehouse.Name, i.QuantityAvailable,
        i.QuantityReserved, i.QuantityFree, i.Product.MinimumStock, i.IsLowStock(i.Product.MinimumStock), i.Product.Cost,
        Money.Round(i.QuantityAvailable * i.Product.Cost), i.UpdatedAt);

    public static MovementDto ToDto(this InventoryMovement m) => new(m.Id, m.Timestamp, m.MovementType.ToString(), m.ProductId,
        m.Product!.ProductCode, m.Product.ProductName, m.WarehouseId, m.Warehouse!.Code, m.Quantity, m.Reference, m.Notes, m.UserName);

    public static SalesOrderSummaryDto ToSummary(this SalesOrder o) => new(o.Id, o.OrderNumber, o.OrderDate, o.Status.ToString(),
        o.CustomerId, o.Customer!.Name, o.Warehouse!.Code, o.Subtotal, o.Tax, o.Total, o.Details.Count);

    public static SalesOrderDto ToDto(this SalesOrder o) => new(o.Id, o.OrderNumber, o.OrderDate, o.Status.ToString(), o.CustomerId,
        o.Customer!.Name, o.Customer.Rfc, o.WarehouseId, o.Warehouse!.Name, o.TaxRate, o.Subtotal, o.Tax, o.Total, o.Notes,
        o.ExternalReference, o.ConfirmedAt, o.ShippedAt,
        o.Details.Select(d => new SalesOrderLineDto(d.Id, d.ProductId, d.Product!.ProductCode, d.Product.ProductName,
            d.Product.Unit, d.Quantity, d.UnitPrice, d.LineTotal)).ToList());

    public static ProductionOrderDto ToDto(this ProductionOrder p) => new(p.Id, p.ProductionNumber, p.ProductId,
        p.Product!.ProductCode, p.Product.ProductName, p.Product.Unit, p.WarehouseId, p.Warehouse!.Code, p.QuantityPlanned,
        p.QuantityCompleted, p.ProgressPercent, p.Status.ToString(), p.DueDate, p.StartDate, p.CompletionDate, p.Notes, p.CreatedAt);

    public static NotificationDto ToDto(this Notification n) => new(n.Id, n.Type.ToString(), n.Severity.ToString(), n.Title,
        n.Message, n.ProductId, n.WarehouseId, n.IsRead, n.IsResolved, n.CreatedAt, n.ResolvedAt);
}
