namespace GlasFlow.Domain.Entities;

public class Customer : AuditableEntity
{
    public string CustomerCode { get; set; } = "";
    /// <summary>Identifier of this customer in an external ERP (integration mapping).</summary>
    public string? ExternalId { get; set; }
    public string Name { get; set; } = "";
    public string? Rfc { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string? PostalCode { get; set; }
    public TaxZone TaxZone { get; set; } = TaxZone.General;
    public decimal CreditLimit { get; set; }
    public bool Active { get; set; } = true;

    public List<SalesOrder> SalesOrders { get; set; } = [];
}

public class Supplier : AuditableEntity
{
    public string SupplierCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Rfc { get; set; }
    public string? ContactName { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public bool Active { get; set; } = true;

    public List<Product> Products { get; set; } = [];
}

public static class ProductCategories
{
    public const string Clear = "Vidrio claro";
    public const string Tempered = "Vidrio templado";
    public const string Laminated = "Vidrio laminado";
    public const string Decorative = "Vidrio decorativo";
    public const string Accessories = "Accesorios";

    public static readonly string[] All = [Clear, Tempered, Laminated, Decorative, Accessories];
}

public class Product : AuditableEntity
{
    public string ProductCode { get; set; } = "";
    /// <summary>SKU of this product in an external ERP (integration mapping).</summary>
    public string? ExternalId { get; set; }
    public string ProductName { get; set; } = "";
    public string Category { get; set; } = "";
    public string? Description { get; set; }
    /// <summary>Unit of measure: m2, pza, ml, kg.</summary>
    public string Unit { get; set; } = "m2";
    public decimal Cost { get; set; }
    public decimal SellingPrice { get; set; }
    /// <summary>Minimum stock per warehouse; below this a low-stock alert is raised.</summary>
    public decimal MinimumStock { get; set; }
    public bool Active { get; set; } = true;
    public int? SupplierId { get; set; }

    public Supplier? Supplier { get; set; }
    public List<InventoryItem> Inventory { get; set; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ProductCode)) throw new DomainException("Product code is required.");
        if (string.IsNullOrWhiteSpace(ProductName)) throw new DomainException("Product name is required.");
        if (!ProductCategories.All.Contains(Category)) throw new DomainException($"Unknown category '{Category}'.");
        if (Cost < 0) throw new DomainException("Cost cannot be negative.");
        if (SellingPrice <= 0) throw new DomainException("Selling price must be greater than zero.");
        if (MinimumStock < 0) throw new DomainException("Minimum stock cannot be negative.");
    }
}

public class Warehouse : AuditableEntity
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public bool Active { get; set; } = true;
}
