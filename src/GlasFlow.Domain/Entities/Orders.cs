namespace GlasFlow.Domain.Entities;

public enum SalesOrderStatus { Draft, Confirmed, Shipped, Cancelled }

public class SalesOrder : AuditableEntity
{
    public string OrderNumber { get; set; } = "";
    public int CustomerId { get; set; }
    /// <summary>Warehouse that fulfils the order.</summary>
    public int WarehouseId { get; set; }
    public DateTime OrderDate { get; set; }
    public SalesOrderStatus Status { get; set; } = SalesOrderStatus.Draft;
    public decimal TaxRate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public string? Notes { get; set; }
    /// <summary>Optional client-supplied key that prevents the same order being created twice.</summary>
    public string? ExternalReference { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    public DateTime? ShippedAt { get; set; }

    public Customer? Customer { get; set; }
    public Warehouse? Warehouse { get; set; }
    public List<SalesOrderDetail> Details { get; set; } = [];

    public void AddLine(Product product, decimal quantity, decimal? unitPrice = null)
    {
        EnsureStatus(SalesOrderStatus.Draft, "add lines to");
        if (!product.Active) throw new DomainException($"Product {product.ProductCode} is inactive.");
        if (quantity <= 0) throw new DomainException("Line quantity must be greater than zero.");
        var price = unitPrice ?? product.SellingPrice;
        if (price <= 0) throw new DomainException("Unit price must be greater than zero.");

        Details.Add(new SalesOrderDetail
        {
            ProductId = product.Id,
            Product = product,
            Quantity = quantity,
            UnitPrice = price,
            LineTotal = Money.Round(quantity * price)
        });
    }

    public void RecalculateTotals(TaxZone zone)
    {
        TaxRate = TaxCalculator.RateFor(zone);
        Subtotal = Money.Round(Details.Sum(d => d.LineTotal));
        Tax = TaxCalculator.TaxFor(Subtotal, zone);
        Total = Subtotal + Tax;
    }

    public void MarkConfirmed(DateTime now)
    {
        EnsureStatus(SalesOrderStatus.Draft, "confirm");
        if (Details.Count == 0) throw new DomainException("Cannot confirm an order without lines.");
        Status = SalesOrderStatus.Confirmed;
        ConfirmedAt = now;
    }

    public void MarkShipped(DateTime now)
    {
        EnsureStatus(SalesOrderStatus.Confirmed, "ship");
        Status = SalesOrderStatus.Shipped;
        ShippedAt = now;
    }

    public void MarkCancelled()
    {
        if (Status is SalesOrderStatus.Shipped or SalesOrderStatus.Cancelled)
            throw new DomainException($"Cannot cancel an order in status {Status}.");
        Status = SalesOrderStatus.Cancelled;
    }

    private void EnsureStatus(SalesOrderStatus expected, string action)
    {
        if (Status != expected)
            throw new DomainException($"Cannot {action} order {OrderNumber} in status {Status}; it must be {expected}.");
    }
}

public class SalesOrderDetail
{
    public int Id { get; set; }
    public int SalesOrderId { get; set; }
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }

    public SalesOrder? SalesOrder { get; set; }
    public Product? Product { get; set; }
}

public enum ProductionStatus { Planned, InProgress, Completed, Cancelled }

public class ProductionOrder : AuditableEntity
{
    public string ProductionNumber { get; set; } = "";
    public int ProductId { get; set; }
    /// <summary>Warehouse that receives the finished goods.</summary>
    public int WarehouseId { get; set; }
    public decimal QuantityPlanned { get; set; }
    public decimal QuantityCompleted { get; set; }
    public ProductionStatus Status { get; set; } = ProductionStatus.Planned;
    public DateTime? DueDate { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? CompletionDate { get; set; }
    public string? Notes { get; set; }

    public Product? Product { get; set; }
    public Warehouse? Warehouse { get; set; }

    public decimal ProgressPercent => QuantityPlanned == 0 ? 0 : Math.Round(QuantityCompleted / QuantityPlanned * 100, 1);

    public void Start(DateTime now)
    {
        if (Status != ProductionStatus.Planned)
            throw new DomainException($"Only planned orders can be started (current: {Status}).");
        Status = ProductionStatus.InProgress;
        StartDate = now;
    }

    public void RegisterCompleted(decimal quantity)
    {
        if (Status != ProductionStatus.InProgress)
            throw new DomainException($"Production can only be registered while in progress (current: {Status}).");
        if (quantity <= 0) throw new DomainException("Completed quantity must be greater than zero.");
        if (QuantityCompleted + quantity > QuantityPlanned)
            throw new DomainException($"Completed quantity would exceed the planned {QuantityPlanned}.");
        QuantityCompleted += quantity;
    }

    public void Complete(DateTime now)
    {
        if (Status != ProductionStatus.InProgress)
            throw new DomainException($"Only in-progress orders can be completed (current: {Status}).");
        if (QuantityCompleted <= 0)
            throw new DomainException("Register at least one completed unit before completing the order.");
        Status = ProductionStatus.Completed;
        CompletionDate = now;
    }

    public void Cancel()
    {
        if (Status is ProductionStatus.Completed or ProductionStatus.Cancelled)
            throw new DomainException($"Cannot cancel a production order in status {Status}.");
        Status = ProductionStatus.Cancelled;
    }
}
