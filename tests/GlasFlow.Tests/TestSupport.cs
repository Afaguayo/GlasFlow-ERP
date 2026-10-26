using GlasFlow.Application.Common;
using GlasFlow.Application.Services;
using GlasFlow.Domain.Entities;
using GlasFlow.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GlasFlow.Tests;

public class FixedClock(DateTime utcNow) : TimeProvider
{
    public DateTime Now { get; set; } = utcNow;
    public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
}

public class TestUser : ICurrentUser
{
    public string UserName => "tester";
}

/// <summary>
/// Real EF Core context on an in-memory SQLite database (kept alive by an open connection), so queries,
/// constraints and transactions run for real. Exposes the application services wired like in production.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    public GlasFlowDbContext Db { get; }
    public FixedClock Clock { get; } = new(new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc));
    public InventoryOptions InventoryOptions { get; } = new();

    public Customer Customer { get; private set; } = null!;
    public Customer BorderCustomer { get; private set; } = null!;
    public Warehouse Main { get; private set; } = null!;
    public Warehouse Border { get; private set; } = null!;
    public Product Tempered { get; private set; } = null!;
    public Product Laminated { get; private set; } = null!;
    public Product Silicone { get; private set; } = null!;

    public TestDb()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Db = new GlasFlowDbContext(new DbContextOptionsBuilder<GlasFlowDbContext>().UseSqlite(_connection).Options);
        Db.Database.EnsureCreated();
        Seed();
    }

    private void Seed()
    {
        var now = Clock.Now;
        Main = new Warehouse { Code = "CUU-01", Name = "Planta", City = "Chihuahua", State = "Chihuahua", CreatedAt = now, UpdatedAt = now };
        Border = new Warehouse { Code = "CJS-01", Name = "CEDIS", City = "Ciudad Juárez", State = "Chihuahua", CreatedAt = now, UpdatedAt = now };
        Customer = new Customer { CustomerCode = "CLI-001", Name = "Constructora Prueba", City = "Chihuahua", State = "Chihuahua", TaxZone = TaxZone.General, CreatedAt = now, UpdatedAt = now };
        BorderCustomer = new Customer { CustomerCode = "CLI-002", Name = "Cristal Juárez", City = "Ciudad Juárez", State = "Chihuahua", TaxZone = TaxZone.FronteraNorte, CreatedAt = now, UpdatedAt = now };
        Tempered = new Product { ProductCode = "TMP-10MM", ProductName = "Templado 10 mm", Category = ProductCategories.Tempered, Unit = "m2", Cost = 890, SellingPrice = 1450, MinimumStock = 50, CreatedAt = now, UpdatedAt = now };
        Laminated = new Product { ProductCode = "LAM-6MM", ProductName = "Laminado 3+3", Category = ProductCategories.Laminated, Unit = "m2", Cost = 760, SellingPrice = 1190, MinimumStock = 20, CreatedAt = now, UpdatedAt = now };
        Silicone = new Product { ProductCode = "ACC-SIL", ProductName = "Silicón", Category = ProductCategories.Accessories, Unit = "pza", Cost = 85, SellingPrice = 145, MinimumStock = 0, CreatedAt = now, UpdatedAt = now };
        Db.AddRange(Main, Border, Customer, BorderCustomer, Tempered, Laminated, Silicone);
        Db.SaveChanges();
        Db.Inventory.AddRange(
            new InventoryItem { ProductId = Tempered.Id, WarehouseId = Main.Id, QuantityAvailable = 100, UpdatedAt = now },
            new InventoryItem { ProductId = Laminated.Id, WarehouseId = Main.Id, QuantityAvailable = 30, UpdatedAt = now },
            new InventoryItem { ProductId = Silicone.Id, WarehouseId = Main.Id, QuantityAvailable = 500, UpdatedAt = now },
            new InventoryItem { ProductId = Tempered.Id, WarehouseId = Border.Id, QuantityAvailable = 10, UpdatedAt = now });
        Db.SaveChanges();
        Db.ChangeTracker.Clear();
    }

    public AuditService Audit => new(Db, new TestUser(), Clock);
    public NotificationService Notifications => new(Db, Clock);
    public InventoryAlertService Alerts => new(Db, Audit, Clock, NullLogger<InventoryAlertService>.Instance);
    public InventoryService Inventory => new(Db, Audit, new TestUser(), Clock, Options.Create(InventoryOptions), Alerts);
    public SalesOrderService Sales => new(Db, Audit, Clock, Inventory, Alerts, Notifications, Options.Create(InventoryOptions));
    public ProductionService Production => new(Db, Audit, Clock, Inventory, Alerts, Notifications);
    public ProductService Products => new(Db, Audit, Clock);
    public ImportService Import => new(Db, Audit, Notifications, Clock);

    public InventoryItem Stock(int productId, int warehouseId) =>
        Db.Inventory.AsNoTracking().Single(i => i.ProductId == productId && i.WarehouseId == warehouseId);

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }
}
