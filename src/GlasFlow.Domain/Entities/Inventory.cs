namespace GlasFlow.Domain.Entities;

/// <summary>
/// Stock of one product in one warehouse (table Inventory).
/// QuantityAvailable is physical on-hand stock; QuantityReserved is the part committed to
/// confirmed sales orders. Free stock = QuantityAvailable - QuantityReserved.
/// </summary>
public class InventoryItem
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public decimal QuantityAvailable { get; set; }
    public decimal QuantityReserved { get; set; }
    public DateTime UpdatedAt { get; set; }
    /// <summary>Optimistic-concurrency token, incremented on every stock change.</summary>
    public int Version { get; set; }

    public Product? Product { get; set; }
    public Warehouse? Warehouse { get; set; }

    public decimal QuantityFree => QuantityAvailable - QuantityReserved;

    public void Reserve(decimal quantity, bool allowNegative = false)
    {
        RequirePositive(quantity);
        if (!allowNegative && QuantityFree < quantity)
            throw new DomainException($"Insufficient stock: requested {quantity}, free {QuantityFree}.");
        QuantityReserved += quantity;
        Touch();
    }

    public void Release(decimal quantity)
    {
        RequirePositive(quantity);
        QuantityReserved = Math.Max(0, QuantityReserved - quantity);
        Touch();
    }

    /// <summary>Removes reserved stock physically (shipment of a confirmed order).</summary>
    public void ShipReserved(decimal quantity)
    {
        RequirePositive(quantity);
        if (QuantityReserved < quantity) throw new DomainException("Cannot ship more than the reserved quantity.");
        QuantityReserved -= quantity;
        QuantityAvailable -= quantity;
        Touch();
    }

    public void Receive(decimal quantity)
    {
        RequirePositive(quantity);
        QuantityAvailable += quantity;
        Touch();
    }

    /// <summary>Signed adjustment (cycle count, breakage, transfers).</summary>
    public void Adjust(decimal delta, bool allowNegative = false)
    {
        if (delta == 0) throw new DomainException("Adjustment quantity cannot be zero.");
        var result = QuantityAvailable + delta;
        if (!allowNegative && result < 0)
            throw new DomainException($"Adjustment would leave negative stock ({result}).");
        if (result < QuantityReserved)
            throw new DomainException($"Adjustment would leave less stock ({result}) than is reserved ({QuantityReserved}).");
        QuantityAvailable = result;
        Touch();
    }

    /// <summary>Low stock = free stock at or below a positive minimum. A minimum of 0 means "not tracked".</summary>
    public bool IsLowStock(decimal minimumStock) => minimumStock > 0 && QuantityFree <= minimumStock;

    private void Touch() => Version++;

    private static void RequirePositive(decimal quantity)
    {
        if (quantity <= 0) throw new DomainException("Quantity must be greater than zero.");
    }
}

public enum MovementType
{
    Adjustment,
    Reservation,
    ReservationRelease,
    SaleShipment,
    ProductionReceipt,
    TransferOut,
    TransferIn,
    Import
}

public class InventoryMovement
{
    public long Id { get; set; }
    public int ProductId { get; set; }
    public int WarehouseId { get; set; }
    public MovementType MovementType { get; set; }
    /// <summary>Signed quantity: positive increases stock, negative decreases it.</summary>
    public decimal Quantity { get; set; }
    /// <summary>Business document that caused the movement, e.g. SO-2026-0001.</summary>
    public string Reference { get; set; } = "";
    public string? Notes { get; set; }
    public DateTime Timestamp { get; set; }
    public string UserName { get; set; } = "";

    public Product? Product { get; set; }
    public Warehouse? Warehouse { get; set; }
}
