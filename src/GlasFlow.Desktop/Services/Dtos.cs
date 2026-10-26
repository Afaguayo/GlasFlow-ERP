namespace GlasFlow.Desktop.Services;

// Client-side shapes of the REST API JSON (camelCase, enums as strings). No business logic lives here.

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public sealed record LoginResponse(string Token, DateTime ExpiresAt, string Username, string DisplayName, string Role);

public sealed record CustomerDto(int Id, string CustomerCode, string Name, string? Rfc, string City, string State, string TaxZone, bool Active)
{
    public override string ToString() => $"{CustomerCode} - {Name}";
}

public sealed record SupplierDto(int Id, string SupplierCode, string Name)
{
    public override string ToString() => Id == 0 ? Name : $"{SupplierCode} - {Name}";
}

public sealed record WarehouseDto(int Id, string Code, string Name, string City, string State, bool Active)
{
    public override string ToString() => Id == 0 ? Name : $"{Code} - {Name}";
}

public sealed record ProductDto(int Id, string ProductCode, string? ExternalId, string ProductName, string Category, string? Description,
    string Unit, decimal Cost, decimal SellingPrice, decimal MinimumStock, bool Active, int? SupplierId, string? SupplierName,
    decimal TotalAvailable, decimal TotalReserved, bool IsLowStock)
{
    public override string ToString() => $"{ProductCode} - {ProductName}";
}

public sealed record StockDto(int InventoryId, int ProductId, string ProductCode, string ProductName, string Category, string Unit,
    int WarehouseId, string WarehouseCode, string WarehouseName, decimal QuantityAvailable, decimal QuantityReserved,
    decimal QuantityFree, decimal MinimumStock, bool IsLowStock, decimal Cost, decimal StockValue, DateTime UpdatedAt);

public sealed record SalesOrderSummaryDto(int Id, string OrderNumber, DateTime OrderDate, string Status, int CustomerId,
    string CustomerName, string WarehouseCode, decimal Subtotal, decimal Tax, decimal Total, int LineCount);

public sealed record SalesOrderLineDto(int Id, int ProductId, string ProductCode, string ProductName, string Unit,
    decimal Quantity, decimal UnitPrice, decimal LineTotal);

public sealed record SalesOrderDto(int Id, string OrderNumber, DateTime OrderDate, string Status, int CustomerId, string CustomerName,
    string? CustomerRfc, int WarehouseId, string WarehouseName, decimal TaxRate, decimal Subtotal, decimal Tax, decimal Total,
    string? Notes, string? ExternalReference, DateTime? ConfirmedAt, DateTime? ShippedAt, IReadOnlyList<SalesOrderLineDto> Lines);

public sealed record KpiDto(decimal SalesThisMonth, decimal SalesLastMonth, decimal SalesYearToDate, int PendingOrders, int ConfirmedOrders,
    decimal InventoryValue, int LowStockProducts, int ActiveProductionOrders, decimal ProductionProgressPercent, int UnreadNotifications);

public sealed record MonthlySalesDto(int Year, int Month, string Label, decimal Total, int Orders);

public sealed record DashboardDto(KpiDto Kpis, IReadOnlyList<MonthlySalesDto> SalesByMonth, IReadOnlyList<StockDto> LowStock,
    IReadOnlyList<SalesOrderSummaryDto> RecentOrders, DateTime GeneratedAt);

// ----- Requests -----
public sealed record StockAdjustmentRequest(int ProductId, int WarehouseId, decimal Quantity, string Reason);

public sealed record ProductRequest(string ProductCode, string? ExternalId, string ProductName, string Category, string? Description,
    string Unit, decimal Cost, decimal SellingPrice, decimal MinimumStock, bool Active, int? SupplierId);

public sealed record SalesOrderLineRequest(int ProductId, decimal Quantity);

public sealed record CreateSalesOrderRequest(int CustomerId, int WarehouseId, string? Notes, string? ExternalReference,
    IReadOnlyList<SalesOrderLineRequest> Lines);
