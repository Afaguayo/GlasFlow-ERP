using GlasFlow.Application.Common;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace GlasFlow.Infrastructure.Persistence;

public class GlasFlowDbContext(DbContextOptions<GlasFlowDbContext> options) : DbContext(options), IGlasFlowDbContext
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<InventoryItem> Inventory => Set<InventoryItem>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<SalesOrderDetail> SalesOrderDetails => Set<SalesOrderDetail>();
    public DbSet<ProductionOrder> ProductionOrders => Set<ProductionOrder>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();

    // Names of the SQL Server triggers in database/05_triggers.sql. EF must know about them so it does not
    // use OUTPUT clauses that SQL Server rejects on tables with triggers.
    public const string InventoryTrigger = "trg_Inventory_Audit";
    public const string MovementTrigger = "trg_InventoryMovements_Audit";
    public const string ProductPriceTrigger = "trg_Products_PriceAudit";
    public const string ProductionTrigger = "trg_ProductionOrders_StatusAudit";

    protected override void OnModelCreating(ModelBuilder b)
    {
        // N'...' Unicode literals are SQL Server syntax; SQLite strings are always Unicode.
        var unicodePrefix = Database.IsSqlServer() ? "N" : "";
        b.Entity<Customer>(e =>
        {
            e.Property(x => x.CustomerCode).HasMaxLength(20);
            e.Property(x => x.ExternalId).HasMaxLength(50);
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Rfc).HasMaxLength(13);
            e.Property(x => x.ContactName).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(150);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.Address).HasMaxLength(250);
            e.Property(x => x.City).HasMaxLength(80);
            e.Property(x => x.State).HasMaxLength(80);
            e.Property(x => x.PostalCode).HasMaxLength(5);
            e.Property(x => x.TaxZone).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.CreditLimit).HasPrecision(18, 2);
            e.HasIndex(x => x.CustomerCode).IsUnique();
            e.HasIndex(x => x.ExternalId).IsUnique().HasFilter("ExternalId IS NOT NULL");
            e.HasIndex(x => x.Name);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_Customers_TaxZone", "TaxZone IN ('General','FronteraNorte')");
                t.HasCheckConstraint("CK_Customers_CreditLimit", "CreditLimit >= 0");
            });
        });

        b.Entity<Supplier>(e =>
        {
            e.Property(x => x.SupplierCode).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(150);
            e.Property(x => x.Rfc).HasMaxLength(13);
            e.Property(x => x.ContactName).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(150);
            e.Property(x => x.Phone).HasMaxLength(30);
            e.Property(x => x.City).HasMaxLength(80);
            e.Property(x => x.State).HasMaxLength(80);
            e.HasIndex(x => x.SupplierCode).IsUnique();
        });

        b.Entity<Warehouse>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.City).HasMaxLength(80);
            e.Property(x => x.State).HasMaxLength(80);
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<Product>(e =>
        {
            e.Property(x => x.ProductCode).HasMaxLength(30);
            e.Property(x => x.ExternalId).HasMaxLength(50);
            e.Property(x => x.ProductName).HasMaxLength(150);
            e.Property(x => x.Category).HasMaxLength(50);
            e.Property(x => x.Description).HasMaxLength(500);
            e.Property(x => x.Unit).HasMaxLength(10);
            e.Property(x => x.Cost).HasPrecision(18, 2);
            e.Property(x => x.SellingPrice).HasPrecision(18, 2);
            e.Property(x => x.MinimumStock).HasPrecision(18, 3);
            e.HasIndex(x => x.ProductCode).IsUnique();
            e.HasIndex(x => x.ExternalId).IsUnique().HasFilter("ExternalId IS NOT NULL");
            e.HasIndex(x => x.Category);
            e.HasOne(x => x.Supplier).WithMany(s => s.Products).HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasTrigger(ProductPriceTrigger);
                t.HasCheckConstraint("CK_Products_Cost", "Cost >= 0");
                t.HasCheckConstraint("CK_Products_SellingPrice", "SellingPrice > 0");
                t.HasCheckConstraint("CK_Products_MinimumStock", "MinimumStock >= 0");
                t.HasCheckConstraint("CK_Products_Unit", "Unit IN ('m2','pza','ml','kg')");
                t.HasCheckConstraint("CK_Products_Category",
                    "Category IN (" + string.Join(",", ProductCategories.All.Select(c => $"{unicodePrefix}'{c}'")) + ")");
            });
        });

        b.Entity<InventoryItem>(e =>
        {
            e.ToTable("Inventory", t =>
            {
                t.HasTrigger(InventoryTrigger);
                t.HasCheckConstraint("CK_Inventory_Reserved", "QuantityReserved >= 0");
            });
            e.Property(x => x.QuantityAvailable).HasPrecision(18, 3);
            e.Property(x => x.QuantityReserved).HasPrecision(18, 3);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.Ignore(x => x.QuantityFree);
            e.HasIndex(x => new { x.ProductId, x.WarehouseId }).IsUnique();
            e.HasIndex(x => x.WarehouseId);
            e.HasOne(x => x.Product).WithMany(p => p.Inventory).HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<SalesOrder>(e =>
        {
            e.Property(x => x.OrderNumber).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.ExternalReference).HasMaxLength(60);
            e.Property(x => x.TaxRate).HasPrecision(5, 4);
            e.Property(x => x.Subtotal).HasPrecision(18, 2);
            e.Property(x => x.Tax).HasPrecision(18, 2);
            e.Property(x => x.Total).HasPrecision(18, 2);
            e.HasIndex(x => x.OrderNumber).IsUnique();
            e.HasIndex(x => x.ExternalReference).IsUnique().HasFilter("ExternalReference IS NOT NULL");
            e.HasIndex(x => new { x.CustomerId, x.OrderDate });
            e.HasIndex(x => new { x.Status, x.OrderDate });
            e.HasOne(x => x.Customer).WithMany(c => c.SalesOrders).HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SalesOrders_Status", "Status IN ('Draft','Confirmed','Shipped','Cancelled')");
                t.HasCheckConstraint("CK_SalesOrders_Amounts", "Subtotal >= 0 AND Tax >= 0 AND Total = Subtotal + Tax");
            });
        });

        b.Entity<SalesOrderDetail>(e =>
        {
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.UnitPrice).HasPrecision(18, 2);
            e.Property(x => x.LineTotal).HasPrecision(18, 2);
            e.HasIndex(x => new { x.SalesOrderId, x.ProductId }).IsUnique();
            e.HasIndex(x => x.ProductId);
            e.HasOne(x => x.SalesOrder).WithMany(o => o.Details).HasForeignKey(x => x.SalesOrderId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasCheckConstraint("CK_SalesOrderDetails_Quantity", "Quantity > 0");
                t.HasCheckConstraint("CK_SalesOrderDetails_UnitPrice", "UnitPrice > 0");
            });
        });

        b.Entity<ProductionOrder>(e =>
        {
            e.Property(x => x.ProductionNumber).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Notes).HasMaxLength(500);
            e.Property(x => x.QuantityPlanned).HasPrecision(18, 3);
            e.Property(x => x.QuantityCompleted).HasPrecision(18, 3);
            e.Ignore(x => x.ProgressPercent);
            e.HasIndex(x => x.ProductionNumber).IsUnique();
            e.HasIndex(x => new { x.Status, x.DueDate });
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasTrigger(ProductionTrigger);
                t.HasCheckConstraint("CK_ProductionOrders_Status", "Status IN ('Planned','InProgress','Completed','Cancelled')");
                t.HasCheckConstraint("CK_ProductionOrders_Quantities", "QuantityPlanned > 0 AND QuantityCompleted >= 0 AND QuantityCompleted <= QuantityPlanned");
            });
        });

        b.Entity<InventoryMovement>(e =>
        {
            e.Property(x => x.MovementType).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Quantity).HasPrecision(18, 3);
            e.Property(x => x.Reference).HasMaxLength(50);
            e.Property(x => x.Notes).HasMaxLength(250);
            e.Property(x => x.UserName).HasMaxLength(50);
            e.HasIndex(x => new { x.ProductId, x.Timestamp });
            e.HasIndex(x => new { x.WarehouseId, x.Timestamp });
            e.HasIndex(x => x.Reference);
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t =>
            {
                t.HasTrigger(MovementTrigger);
                t.HasCheckConstraint("CK_InventoryMovements_Type",
                    "MovementType IN (" + string.Join(",", Enum.GetNames<MovementType>().Select(n => $"'{n}'")) + ")");
            });
        });

        b.Entity<User>(e =>
        {
            e.Property(x => x.Username).HasMaxLength(50);
            e.Property(x => x.DisplayName).HasMaxLength(100);
            e.Property(x => x.Email).HasMaxLength(150);
            e.Property(x => x.PasswordHash).HasMaxLength(200);
            e.Property(x => x.Role).HasMaxLength(20);
            e.HasIndex(x => x.Username).IsUnique();
            e.ToTable(t => t.HasCheckConstraint("CK_Users_Role", "Role IN ('Admin','Operator','Viewer')"));
        });

        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.UserName).HasMaxLength(50);
            e.Property(x => x.Action).HasMaxLength(60);
            e.Property(x => x.EntityType).HasMaxLength(60);
            e.Property(x => x.EntityId).HasMaxLength(60);
            e.Property(x => x.Source).HasMaxLength(20);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        b.Entity<Notification>(e =>
        {
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(150);
            e.Property(x => x.Message).HasMaxLength(500);
            e.Property(x => x.DedupKey).HasMaxLength(80);
            // At most one open notification per condition: the database itself prevents duplicate alerts.
            e.HasIndex(x => x.DedupKey).IsUnique().HasFilter("DedupKey IS NOT NULL AND IsResolved = 0");
            e.HasIndex(x => new { x.IsRead, x.CreatedAt });
            e.HasOne(x => x.Product).WithMany().HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Warehouse).WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        // All timestamps are stored in UTC; mark them as such when read so the API serializes them with "Z".
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        foreach (var prop in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
            else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
        }

        if (Database.IsSqlite())
        {
            // SQLite has no decimal type and cannot aggregate decimals; store them as REAL for the fallback provider.
            foreach (var prop in b.Model.GetEntityTypes().SelectMany(t => t.GetProperties())
                         .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?)))
                prop.SetProviderClrType(typeof(double));
        }
    }

}
