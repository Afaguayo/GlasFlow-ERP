using System.ComponentModel.DataAnnotations;

namespace GlasFlow.Application.Dtos;

// ---------- Customers ----------
public record CustomerDto(int Id, string CustomerCode, string? ExternalId, string Name, string? Rfc, string? ContactName,
    string? Email, string? Phone, string? Address, string City, string State, string? PostalCode, string TaxZone,
    decimal CreditLimit, bool Active, DateTime CreatedAt, DateTime UpdatedAt);

public record CustomerDetailDto(CustomerDto Customer, decimal TotalSales, int OrderCount, IReadOnlyList<SalesOrderSummaryDto> RecentOrders);

public class CustomerRequest
{
    [Required, StringLength(20, MinimumLength = 2)] public string CustomerCode { get; set; } = "";
    [StringLength(50)] public string? ExternalId { get; set; }
    [Required, StringLength(150, MinimumLength = 2)] public string Name { get; set; } = "";
    [RegularExpression(@"^[A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3}$", ErrorMessage = "RFC must have the SAT format (e.g. VCJ850101AB1).")]
    public string? Rfc { get; set; }
    [StringLength(100)] public string? ContactName { get; set; }
    [EmailAddress, StringLength(150)] public string? Email { get; set; }
    [StringLength(30)] public string? Phone { get; set; }
    [StringLength(250)] public string? Address { get; set; }
    [Required, StringLength(80)] public string City { get; set; } = "";
    [Required, StringLength(80)] public string State { get; set; } = "";
    [RegularExpression(@"^\d{5}$", ErrorMessage = "Postal code must have 5 digits.")] public string? PostalCode { get; set; }
    [RegularExpression("^(General|FronteraNorte)$", ErrorMessage = "TaxZone must be General or FronteraNorte.")]
    public string TaxZone { get; set; } = "General";
    [Range(0, 100_000_000)] public decimal CreditLimit { get; set; }
    public bool Active { get; set; } = true;
}

// ---------- Suppliers ----------
public record SupplierDto(int Id, string SupplierCode, string Name, string? Rfc, string? ContactName, string? Email,
    string? Phone, string City, string State, bool Active, int ProductCount);

public class SupplierRequest
{
    [Required, StringLength(20, MinimumLength = 2)] public string SupplierCode { get; set; } = "";
    [Required, StringLength(150, MinimumLength = 2)] public string Name { get; set; } = "";
    [RegularExpression(@"^[A-ZÑ&]{3,4}\d{6}[A-Z0-9]{3}$", ErrorMessage = "RFC must have the SAT format.")] public string? Rfc { get; set; }
    [StringLength(100)] public string? ContactName { get; set; }
    [EmailAddress, StringLength(150)] public string? Email { get; set; }
    [StringLength(30)] public string? Phone { get; set; }
    [Required, StringLength(80)] public string City { get; set; } = "";
    [Required, StringLength(80)] public string State { get; set; } = "";
    public bool Active { get; set; } = true;
}

// ---------- Products / Warehouses ----------
public record ProductDto(int Id, string ProductCode, string? ExternalId, string ProductName, string Category, string? Description,
    string Unit, decimal Cost, decimal SellingPrice, decimal MinimumStock, bool Active, int? SupplierId, string? SupplierName,
    decimal TotalAvailable, decimal TotalReserved, bool IsLowStock);

public class ProductRequest
{
    [Required, StringLength(30, MinimumLength = 2)] public string ProductCode { get; set; } = "";
    [StringLength(50)] public string? ExternalId { get; set; }
    [Required, StringLength(150, MinimumLength = 2)] public string ProductName { get; set; } = "";
    [Required] public string Category { get; set; } = "";
    [StringLength(500)] public string? Description { get; set; }
    [Required, RegularExpression("^(m2|pza|ml|kg)$", ErrorMessage = "Unit must be m2, pza, ml or kg.")] public string Unit { get; set; } = "m2";
    [Range(0, 10_000_000)] public decimal Cost { get; set; }
    [Range(0.01, 10_000_000)] public decimal SellingPrice { get; set; }
    [Range(0, 10_000_000)] public decimal MinimumStock { get; set; }
    public bool Active { get; set; } = true;
    public int? SupplierId { get; set; }
}

public record WarehouseDto(int Id, string Code, string Name, string City, string State, bool Active, int ProductLines, decimal InventoryValue);

public class WarehouseRequest
{
    [Required, StringLength(20, MinimumLength = 2)] public string Code { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Required, StringLength(80)] public string City { get; set; } = "";
    [Required, StringLength(80)] public string State { get; set; } = "";
    public bool Active { get; set; } = true;
}

// ---------- Inventory ----------
public record StockDto(int InventoryId, int ProductId, string ProductCode, string ProductName, string Category, string Unit,
    int WarehouseId, string WarehouseCode, string WarehouseName, decimal QuantityAvailable, decimal QuantityReserved,
    decimal QuantityFree, decimal MinimumStock, bool IsLowStock, decimal Cost, decimal StockValue, DateTime UpdatedAt);

public record StockQuery(int Page = 1, int PageSize = 50, string? Search = null, string? Category = null,
    int? WarehouseId = null, bool LowStockOnly = false);

public record MovementDto(long Id, DateTime Timestamp, string MovementType, int ProductId, string ProductCode, string ProductName,
    int WarehouseId, string WarehouseCode, decimal Quantity, string Reference, string? Notes, string UserName);

public record MovementQuery(int Page = 1, int PageSize = 50, int? ProductId = null, int? WarehouseId = null,
    string? MovementType = null, DateTime? From = null, DateTime? To = null);

public class StockAdjustmentRequest
{
    [Range(1, int.MaxValue)] public int ProductId { get; set; }
    [Range(1, int.MaxValue)] public int WarehouseId { get; set; }
    /// <summary>Signed delta: positive adds stock, negative removes it.</summary>
    [Range(-1_000_000, 1_000_000)] public decimal Quantity { get; set; }
    [Required, StringLength(200, MinimumLength = 3)] public string Reason { get; set; } = "";
}

public class StockTransferRequest
{
    [Range(1, int.MaxValue)] public int ProductId { get; set; }
    [Range(1, int.MaxValue)] public int FromWarehouseId { get; set; }
    [Range(1, int.MaxValue)] public int ToWarehouseId { get; set; }
    [Range(0.001, 1_000_000)] public decimal Quantity { get; set; }
    [StringLength(200)] public string? Notes { get; set; }
}

// ---------- Sales ----------
public record SalesOrderSummaryDto(int Id, string OrderNumber, DateTime OrderDate, string Status, int CustomerId,
    string CustomerName, string WarehouseCode, decimal Subtotal, decimal Tax, decimal Total, int LineCount);

public record SalesOrderLineDto(int Id, int ProductId, string ProductCode, string ProductName, string Unit,
    decimal Quantity, decimal UnitPrice, decimal LineTotal);

public record SalesOrderDto(int Id, string OrderNumber, DateTime OrderDate, string Status, int CustomerId, string CustomerName,
    string? CustomerRfc, int WarehouseId, string WarehouseName, decimal TaxRate, decimal Subtotal, decimal Tax, decimal Total,
    string? Notes, string? ExternalReference, DateTime? ConfirmedAt, DateTime? ShippedAt, IReadOnlyList<SalesOrderLineDto> Lines);

public record SalesOrderQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Status = null,
    int? CustomerId = null, DateTime? From = null, DateTime? To = null);

public class SalesOrderLineRequest
{
    [Range(1, int.MaxValue)] public int ProductId { get; set; }
    [Range(0.001, 1_000_000)] public decimal Quantity { get; set; }
    /// <summary>Optional price override; defaults to the product's selling price.</summary>
    [Range(0.01, 10_000_000)] public decimal? UnitPrice { get; set; }
}

public class CreateSalesOrderRequest
{
    [Range(1, int.MaxValue)] public int CustomerId { get; set; }
    [Range(1, int.MaxValue)] public int WarehouseId { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
    /// <summary>Idempotency key: creating two orders with the same value is rejected.</summary>
    [StringLength(60)] public string? ExternalReference { get; set; }
    [Required, MinLength(1, ErrorMessage = "An order needs at least one line.")]
    public List<SalesOrderLineRequest> Lines { get; set; } = [];
}

// ---------- Production ----------
public record ProductionOrderDto(int Id, string ProductionNumber, int ProductId, string ProductCode, string ProductName,
    string Unit, int WarehouseId, string WarehouseCode, decimal QuantityPlanned, decimal QuantityCompleted, decimal ProgressPercent,
    string Status, DateTime? DueDate, DateTime? StartDate, DateTime? CompletionDate, string? Notes, DateTime CreatedAt);

public record ProductionQuery(int Page = 1, int PageSize = 25, string? Search = null, string? Status = null);

public class CreateProductionOrderRequest
{
    [Range(1, int.MaxValue)] public int ProductId { get; set; }
    [Range(1, int.MaxValue)] public int WarehouseId { get; set; }
    [Range(0.001, 1_000_000)] public decimal QuantityPlanned { get; set; }
    public DateTime? DueDate { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
}

public class RegisterProductionRequest
{
    [Range(0.001, 1_000_000)] public decimal Quantity { get; set; }
}

public class CompleteProductionRequest
{
    /// <summary>Optional final quantity registered in the same transaction; omit to complete with what was registered.</summary>
    [Range(0.001, 1_000_000)] public decimal? Quantity { get; set; }
}

// ---------- Notifications / Automation ----------
public record NotificationDto(int Id, string Type, string Severity, string Title, string Message, int? ProductId,
    int? WarehouseId, bool IsRead, bool IsResolved, DateTime CreatedAt, DateTime? ResolvedAt);

public record InventoryCheckResult(DateTime RanAt, int ItemsChecked, int LowStockItems, int NotificationsCreated, int NotificationsResolved);

// ---------- Dashboard ----------
public record KpiDto(decimal SalesThisMonth, decimal SalesLastMonth, decimal SalesYearToDate, int PendingOrders, int ConfirmedOrders,
    decimal InventoryValue, int LowStockProducts, int ActiveProductionOrders, decimal ProductionProgressPercent, int UnreadNotifications);

public record MonthlySalesDto(int Year, int Month, string Label, decimal Total, int Orders);
public record CategoryValueDto(string Category, decimal Value);

public record DashboardDto(KpiDto Kpis, IReadOnlyList<MonthlySalesDto> SalesByMonth, IReadOnlyList<CategoryValueDto> InventoryByCategory,
    IReadOnlyList<MovementDto> RecentMovements, IReadOnlyList<StockDto> LowStock, IReadOnlyList<ProductionOrderDto> ActiveProduction,
    IReadOnlyList<SalesOrderSummaryDto> RecentOrders, DateTime GeneratedAt);

// ---------- Reports ----------
public record SalesReportRow(string OrderNumber, DateTime OrderDate, string CustomerCode, string CustomerName, string Status,
    decimal Subtotal, decimal Tax, decimal Total);

public record InventoryReportRow(string ProductCode, string ProductName, string Category, string WarehouseCode, string Unit,
    decimal QuantityAvailable, decimal QuantityReserved, decimal QuantityFree, decimal MinimumStock, decimal UnitCost, decimal StockValue, bool IsLowStock);

public record ProductionReportRow(string ProductionNumber, string ProductCode, string ProductName, string Status,
    decimal QuantityPlanned, decimal QuantityCompleted, decimal ProgressPercent, DateTime? StartDate, DateTime? CompletionDate, DateTime? DueDate);

public record ReportResult<T>(string Title, DateTime GeneratedAt, DateTime? From, DateTime? To, IReadOnlyList<T> Rows, IReadOnlyDictionary<string, decimal> Totals);

// ---------- Import ----------
public record ImportRowError(int LineNumber, string? ExternalId, string Message);

public record ImportResult(int TotalRows, int Created, int Updated, int Skipped, IReadOnlyList<ImportRowError> Errors, bool DryRun);

// ---------- Auth ----------
public class LoginRequest
{
    [Required, StringLength(50)] public string Username { get; set; } = "";
    [Required, StringLength(100)] public string Password { get; set; } = "";
}

public record LoginResponse(string Token, DateTime ExpiresAt, string Username, string DisplayName, string Role);

// ---------- AI ----------
public class AssistantRequest
{
    [Required, StringLength(500, MinimumLength = 3)] public string Question { get; set; } = "";
}

public record AssistantResponse(string Answer, string Intent, string Provider, bool UsedFallback, object? Data, DateTime GeneratedAt);
