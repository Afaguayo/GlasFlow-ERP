using GlasFlow.Domain.Entities;
using GlasFlow.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GlasFlow.Infrastructure.Persistence;

/// <summary>
/// Idempotent seed of fictional demo data. Master data is inserted by business key (code) only when missing;
/// transactional history is generated once, only when the database has no sales orders.
/// The generated history is internally consistent: opening balance + all movements = current on-hand stock,
/// and reserved stock equals the lines of confirmed orders.
/// </summary>
public class DataSeeder(GlasFlowDbContext db, TimeProvider clock, ILogger<DataSeeder> logger)
{
    private const string SeedUser = "seed";

    public static readonly (string Code, string Name, string City, string State)[] WarehouseData =
    [
        ("CUU-01", "Planta Chihuahua – Complejo Industrial", "Chihuahua", "Chihuahua"),
        ("CJS-01", "CEDIS Ciudad Juárez", "Ciudad Juárez", "Chihuahua")
    ];

    public static readonly (string Code, string Name, string Rfc, string Contact, string City, string State)[] SupplierData =
    [
        ("PRV-001", "Cristales Flotados del Norte S.A. de C.V.", "CFN050412AB1", "Ing. Ramiro Fuentes", "Monterrey", "Nuevo León"),
        ("PRV-002", "Herrajes y Perfiles Industriales S.A. de C.V.", "HPI100823CD2", "Lic. Sofía Treviño", "Saltillo", "Coahuila"),
        ("PRV-003", "Películas PVB de Occidente S.A. de C.V.", "PPO120301EF3", "Ing. Marco Ibarra", "Guadalajara", "Jalisco"),
        ("PRV-004", "Selladores y Químicos del Bajío S.A. de C.V.", "SQB090917GH4", "C.P. Laura Méndez", "León", "Guanajuato")
    ];

    public static readonly (string Code, string Name, string Category, string Unit, decimal Cost, decimal Price, decimal Min, string Supplier, string Description)[] ProductData =
    [
        ("CLR-4MM", "Vidrio claro flotado 4 mm", ProductCategories.Clear, "m2", 210m, 340m, 120m, "PRV-001", "Hoja de vidrio flotado claro para ventanería residencial."),
        ("CLR-6MM", "Vidrio claro flotado 6 mm", ProductCategories.Clear, "m2", 300m, 480m, 100m, "PRV-001", "Vidrio claro para cancelería y aparadores."),
        ("CLR-10MM", "Vidrio claro flotado 10 mm", ProductCategories.Clear, "m2", 520m, 820m, 40m, "PRV-001", "Vidrio grueso para cubiertas y mostradores."),
        ("TMP-6MM", "Vidrio templado 6 mm", ProductCategories.Tempered, "m2", 620m, 980m, 60m, "PRV-001", "Templado térmicamente; ventanas y puertas de seguridad."),
        ("TMP-10MM", "Vidrio templado 10 mm", ProductCategories.Tempered, "m2", 890m, 1450m, 50m, "PRV-001", "Canceles de baño y puertas de vidrio templado."),
        ("TMP-12MM", "Vidrio templado 12 mm para barandal", ProductCategories.Tempered, "m2", 1100m, 1780m, 30m, "PRV-001", "Barandales y fachadas con herrajes de punto fijo."),
        ("LAM-6MM", "Vidrio laminado 3+3 mm PVB claro", ProductCategories.Laminated, "m2", 760m, 1190m, 50m, "PRV-003", "Laminado de seguridad para ventanas y domos."),
        ("LAM-8MM", "Vidrio laminado 4+4 mm seguridad", ProductCategories.Laminated, "m2", 940m, 1490m, 40m, "PRV-003", "Laminado para fachadas y zonas de alto tránsito."),
        ("LAM-ACU", "Vidrio laminado acústico 5+5 mm", ProductCategories.Laminated, "m2", 1350m, 2150m, 20m, "PRV-003", "Laminado con PVB acústico para oficinas y hoteles."),
        ("DEC-ESM", "Vidrio esmerilado 6 mm", ProductCategories.Decorative, "m2", 480m, 760m, 40m, "PRV-001", "Acabado satinado al ácido para privacidad."),
        ("DEC-REF", "Vidrio reflejante bronce 6 mm", ProductCategories.Decorative, "m2", 520m, 840m, 30m, "PRV-001", "Control solar para fachadas comerciales."),
        ("DEC-ESP", "Espejo plata 4 mm", ProductCategories.Decorative, "m2", 380m, 620m, 60m, "PRV-001", "Espejo con capa de plata y pintura protectora."),
        ("ACC-SIL", "Silicón estructural transparente 300 ml", ProductCategories.Accessories, "pza", 85m, 145m, 200m, "PRV-004", "Sellador de silicón neutro para vidrio y aluminio."),
        ("ACC-BIS", "Bisagra hidráulica de acero inoxidable", ProductCategories.Accessories, "pza", 650m, 1090m, 40m, "PRV-002", "Bisagra para puertas de vidrio templado de 10–12 mm."),
        ("ACC-PERF", "Perfil de aluminio para cancel 6 m", ProductCategories.Accessories, "pza", 420m, 690m, 60m, "PRV-002", "Perfil natural anodizado para canceles corredizos."),
        ("ACC-ARA", "Araña de acero inoxidable 4 puntos", ProductCategories.Accessories, "pza", 980m, 1590m, 20m, "PRV-002", "Herraje de punto fijo para fachadas de vidrio.")
    ];

    public static readonly (string Code, string Name, string Rfc, string Contact, string Address, string City, string State, string Cp, TaxZone Zone, decimal Credit)[] CustomerData =
    [
        ("CLI-001", "Constructora Cumbres del Norte S.A. de C.V.", "CCN100315AB1", "Ing. Alejandro Rivas", "Av. Tecnológico 4520, Col. Las Granjas", "Chihuahua", "Chihuahua", "31100", TaxZone.General, 750000m),
        ("CLI-002", "Aluminio y Cristal Juárez S. de R.L. de C.V.", "ACJ120601KL2", "Sra. Patricia Holguín", "Blvd. Tomás Fernández 7810", "Ciudad Juárez", "Chihuahua", "32460", TaxZone.FronteraNorte, 400000m),
        ("CLI-003", "Desarrollos Inmobiliarios Sierra Madre S.A. de C.V.", "DIS150920MN3", "Arq. Fernanda Loya", "Periférico de la Juventud 3100", "Chihuahua", "Chihuahua", "31207", TaxZone.General, 1200000m),
        ("CLI-004", "Vidriería La Paz de Delicias", "VLP081105QR4", "Sr. Jesús Armendáriz", "Av. 6a Oriente 215, Centro", "Delicias", "Chihuahua", "33000", TaxZone.General, 150000m),
        ("CLI-005", "Cancelería Moderna del Desierto S.A. de C.V.", "CMD170210ST5", "Sr. Óscar Villalobos", "Av. de las Torres 1450", "Ciudad Juárez", "Chihuahua", "32575", TaxZone.FronteraNorte, 300000m),
        ("CLI-006", "Hotelera Plaza Real del Centro S.A. de C.V.", "HPR990712UV6", "Lic. Mariana Quintana", "Av. Independencia 1100, Centro", "Chihuahua", "Chihuahua", "31000", TaxZone.General, 500000m),
        ("CLI-007", "Arquitectura y Fachadas Bravo S.C.", "AFB130418WX7", "Arq. Daniel Bravo", "Av. Paseo Triunfo de la República 3200", "Ciudad Juárez", "Chihuahua", "32330", TaxZone.FronteraNorte, 650000m),
        ("CLI-008", "Muebles y Espejos de Parral", "MEP110830YZ8", "Sra. Rosa Chávez", "Calle Mercaderes 45, Centro", "Hidalgo del Parral", "Chihuahua", "33800", TaxZone.General, 120000m),
        ("CLI-009", "Grupo Constructor Valle de Cuauhtémoc S.A. de C.V.", "GCV160505AB9", "Ing. Peter Wiebe", "Calle Allende 1020", "Cuauhtémoc", "Chihuahua", "31500", TaxZone.General, 450000m),
        ("CLI-010", "Torres Ejecutivas del Norte S.A. de C.V.", "TEN180922CD1", "Lic. Ricardo Garza", "Av. Constitución 2050, Centro", "Monterrey", "Nuevo León", "64000", TaxZone.General, 900000m),
        ("CLI-011", "Remodelaciones Hogar Feliz", "RHF190314EF2", "Sra. Lucía Domínguez", "Calle Victoria 820, Centro", "Chihuahua", "Chihuahua", "31000", TaxZone.General, 80000m),
        ("CLI-012", "Cristalería Industrial de Torreón S.A. de C.V.", "CIT140727GH3", "Ing. Héctor Salas", "Blvd. Revolución 1500", "Torreón", "Coahuila", "27000", TaxZone.General, 350000m)
    ];

    /// <summary>Free-stock targets that deliberately leave some items under their minimum so alerts are visible.</summary>
    private static readonly Dictionary<(string Product, string Warehouse), decimal> LowStockTargets = new()
    {
        [("TMP-12MM", "CJS-01")] = 12m,
        [("LAM-ACU", "CUU-01")] = 8m,
        [("LAM-ACU", "CJS-01")] = 0m,
        [("ACC-BIS", "CUU-01")] = 25m,
        [("CLR-10MM", "CJS-01")] = 30m
    };

    public async Task SeedAsync(string? demoPassword, bool isDevelopment, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await SeedMasterDataAsync(now, ct);
        await SeedUsersAsync(demoPassword, isDevelopment, now, ct);
        if (!await db.SalesOrders.AnyAsync(ct)) await SeedHistoryAsync(now, ct);
    }

    private async Task SeedMasterDataAsync(DateTime now, CancellationToken ct)
    {
        var created = now.AddMonths(-12);
        foreach (var w in WarehouseData.Where(w => !db.Warehouses.Any(x => x.Code == w.Code)))
            db.Warehouses.Add(new Warehouse { Code = w.Code, Name = w.Name, City = w.City, State = w.State, CreatedAt = created, UpdatedAt = created });

        var i = 0;
        foreach (var s in SupplierData)
        {
            i++;
            if (await db.Suppliers.AnyAsync(x => x.SupplierCode == s.Code, ct)) continue;
            db.Suppliers.Add(new Supplier
            {
                SupplierCode = s.Code, Name = s.Name, Rfc = s.Rfc, ContactName = s.Contact, City = s.City, State = s.State,
                Email = $"ventas{i}@proveedor-{i}.example", Phone = $"81 5550 01{i:00}", CreatedAt = created, UpdatedAt = created
            });
        }

        i = 0;
        foreach (var c in CustomerData)
        {
            i++;
            if (await db.Customers.AnyAsync(x => x.CustomerCode == c.Code, ct)) continue;
            db.Customers.Add(new Customer
            {
                CustomerCode = c.Code, ExternalId = $"ERP-C{1000 + i}", Name = c.Name, Rfc = c.Rfc, ContactName = c.Contact,
                Address = c.Address, City = c.City, State = c.State, PostalCode = c.Cp, TaxZone = c.Zone, CreditLimit = c.Credit,
                Email = $"compras@cliente{i:00}.example", Phone = $"614 555 {i:0000}", CreatedAt = created, UpdatedAt = created
            });
        }
        await db.SaveChangesAsync(ct);

        var suppliers = await db.Suppliers.ToDictionaryAsync(s => s.SupplierCode, s => s.Id, ct);
        i = 0;
        foreach (var p in ProductData)
        {
            i++;
            if (await db.Products.AnyAsync(x => x.ProductCode == p.Code, ct)) continue;
            db.Products.Add(new Product
            {
                ProductCode = p.Code, ExternalId = $"SKU-{20000 + i * 7}", ProductName = p.Name, Category = p.Category, Unit = p.Unit,
                Cost = p.Cost, SellingPrice = p.Price, MinimumStock = p.Min, Description = p.Description,
                SupplierId = suppliers.GetValueOrDefault(p.Supplier), CreatedAt = created, UpdatedAt = created
            });
        }
        await db.SaveChangesAsync(ct);

        // Every product has a stock row in every warehouse.
        var existing = await db.Inventory.Select(x => new { x.ProductId, x.WarehouseId }).ToListAsync(ct);
        var have = existing.Select(x => (x.ProductId, x.WarehouseId)).ToHashSet();
        foreach (var p in await db.Products.Select(p => p.Id).ToListAsync(ct))
        foreach (var w in await db.Warehouses.Select(w => w.Id).ToListAsync(ct))
            if (!have.Contains((p, w))) db.Inventory.Add(new InventoryItem { ProductId = p, WarehouseId = w, UpdatedAt = created });
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedUsersAsync(string? demoPassword, bool isDevelopment, DateTime now, CancellationToken ct)
    {
        (string User, string Name, string Role)[] users =
        [
            ("admin", "Administrador Demo", Roles.Admin),
            ("operador", "Operador de Planta", Roles.Operator),
            ("consulta", "Usuario de Consulta", Roles.Viewer)
        ];
        var existing = await db.Users.ToListAsync(ct);

        if (string.IsNullOrWhiteSpace(demoPassword))
        {
            if (existing.Count > 0) return;
            if (!isDevelopment)
            {
                logger.LogWarning("No Auth:DemoPassword configured; demo users were not created.");
                return;
            }
            demoPassword = PasswordHasher.GeneratePassword();
            logger.LogWarning("Generated demo password for users admin/operador/consulta: {Password} " +
                              "(set Auth__DemoPassword to choose your own).", demoPassword);
        }

        var hash = PasswordHasher.Hash(demoPassword);
        foreach (var u in users)
        {
            var user = existing.FirstOrDefault(x => x.Username == u.User);
            if (user is null)
                db.Users.Add(new User { Username = u.User, DisplayName = u.Name, Role = u.Role, PasswordHash = hash, Email = $"{u.User}@glasflow.example", CreatedAt = now });
            else if (!PasswordHasher.Verify(demoPassword, user.PasswordHash))
                user.PasswordHash = hash; // Development convenience: the configured demo password always works.
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedHistoryAsync(DateTime now, CancellationToken ct)
    {
        var rnd = new Random(2026);
        var customers = await db.Customers.ToListAsync(ct);
        var products = await db.Products.ToListAsync(ct);
        var warehouses = await db.Warehouses.ToDictionaryAsync(w => w.Code, ct);
        var stock = await db.Inventory.ToListAsync(ct);
        var movements = new List<InventoryMovement>();
        var shipped = new Dictionary<(int, int), decimal>();
        var produced = new Dictionary<(int, int), decimal>();
        var reserved = new Dictionary<(int, int), decimal>();
        var glass = products.Where(p => p.Category != ProductCategories.Accessories).ToList();
        var accessories = products.Where(p => p.Category == ProductCategories.Accessories).ToList();

        // ---- Sales orders: 12 months of history ----
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var orders = new List<SalesOrder>();
        for (var m = 11; m >= 0; m--)
        {
            var start = monthStart.AddMonths(-m);
            var count = 4 + rnd.Next(4) + (11 - m) / 4; // gentle growth trend
            for (var k = 0; k < count; k++)
            {
                var date = start.AddDays(rnd.Next(0, 28)).AddHours(14 + rnd.Next(9)) /* 08:00-17:00 in Chihuahua (UTC-6) */.AddMinutes(rnd.Next(60));
                if (date > now.AddHours(-1)) date = now.AddHours(-1 - rnd.Next(48));
                var customer = customers[rnd.Next(customers.Count)];
                var warehouse = customer.TaxZone == TaxZone.FronteraNorte ? warehouses["CJS-01"] : warehouses["CUU-01"];
                var order = new SalesOrder { CustomerId = customer.Id, Customer = customer, WarehouseId = warehouse.Id, OrderDate = date, CreatedAt = date, UpdatedAt = date };
                foreach (var p in glass.OrderBy(_ => rnd.Next()).Take(1 + rnd.Next(3)))
                    order.AddLine(p, 8 + rnd.Next(18) * 2.5m);
                if (rnd.NextDouble() < 0.6)
                    foreach (var p in accessories.OrderBy(_ => rnd.Next()).Take(1 + rnd.Next(2)))
                        order.AddLine(p, 4 + rnd.Next(30));
                order.RecalculateTotals(customer.TaxZone);
                orders.Add(order);
            }
        }

        var counters = new Dictionary<int, int>();
        foreach (var order in orders.OrderBy(o => o.OrderDate))
        {
            var year = order.OrderDate.Year;
            counters[year] = counters.GetValueOrDefault(year) + 1;
            order.OrderNumber = $"SO-{year}-{counters[year]:0000}";
            var age = (now - order.OrderDate).TotalDays;
            var roll = rnd.NextDouble();

            if (age > 20 && roll < 0.08) order.MarkCancelled();
            else if (age > 20 || roll < 0.4)
            {
                order.MarkConfirmed(order.OrderDate.AddHours(2));
                var shipAt = order.OrderDate.AddDays(2) > now ? now.AddMinutes(-30) : order.OrderDate.AddDays(2);
                order.MarkShipped(shipAt);
                foreach (var d in order.Details)
                {
                    Add(shipped, (d.ProductId, order.WarehouseId), d.Quantity);
                    movements.Add(Move(d.ProductId, order.WarehouseId, MovementType.SaleShipment, -d.Quantity, order.OrderNumber, shipAt));
                }
            }
            else if (roll < 0.75)
            {
                order.MarkConfirmed(order.OrderDate.AddHours(2));
                foreach (var d in order.Details)
                {
                    Add(reserved, (d.ProductId, order.WarehouseId), d.Quantity);
                    movements.Add(Move(d.ProductId, order.WarehouseId, MovementType.Reservation, d.Quantity, order.OrderNumber, order.ConfirmedAt!.Value, "Reserved for order"));
                }
            }
            order.UpdatedAt = order.ShippedAt ?? order.ConfirmedAt ?? order.OrderDate;
        }
        db.SalesOrders.AddRange(orders);

        // ---- Production orders ----
        var plan = new (string Product, string Wh, decimal Qty, int StartedDaysAgo, ProductionStatus Status, decimal Done, int DueInDays)[]
        {
            ("TMP-10MM", "CUU-01", 180, 150, ProductionStatus.Completed, 180, -140),
            ("LAM-6MM", "CUU-01", 120, 120, ProductionStatus.Completed, 120, -110),
            ("TMP-6MM", "CJS-01", 150, 90, ProductionStatus.Completed, 145, -80),
            ("DEC-ESM", "CUU-01", 80, 60, ProductionStatus.Completed, 80, -50),
            ("TMP-12MM", "CUU-01", 90, 35, ProductionStatus.Completed, 90, -28),
            ("LAM-8MM", "CJS-01", 100, 12, ProductionStatus.InProgress, 55, 4),
            ("TMP-10MM", "CJS-01", 140, 6, ProductionStatus.InProgress, 30, 10),
            ("DEC-ESP", "CUU-01", 120, 3, ProductionStatus.InProgress, 96, -1),
            ("LAM-ACU", "CUU-01", 60, 0, ProductionStatus.Planned, 0, 14),
            ("TMP-12MM", "CJS-01", 70, 0, ProductionStatus.Planned, 0, 21),
            ("CLR-10MM", "CJS-01", 50, 0, ProductionStatus.Cancelled, 0, 30)
        };
        var byCode = products.ToDictionary(p => p.ProductCode);
        var poCounters = new Dictionary<int, int>();
        foreach (var p in plan.OrderByDescending(x => x.StartedDaysAgo))
        {
            var product = byCode[p.Product];
            var wh = warehouses[p.Wh];
            var created = now.AddDays(-p.StartedDaysAgo - 2);
            poCounters[created.Year] = poCounters.GetValueOrDefault(created.Year) + 1;
            var po = new ProductionOrder
            {
                ProductionNumber = $"PO-{created.Year}-{poCounters[created.Year]:0000}", ProductId = product.Id, WarehouseId = wh.Id,
                QuantityPlanned = p.Qty, DueDate = now.AddDays(p.DueInDays).Date, CreatedAt = created, UpdatedAt = created,
                Notes = p.Status == ProductionStatus.Cancelled ? "Cancelada: pedido del cliente reprogramado." : null
            };
            if (p.Status is ProductionStatus.InProgress or ProductionStatus.Completed)
            {
                var started = now.AddDays(-p.StartedDaysAgo);
                po.Start(started);
                po.RegisterCompleted(p.Done);
                var receivedAt = p.Status == ProductionStatus.Completed ? started.AddDays(6) : now.AddHours(-3);
                Add(produced, (product.Id, wh.Id), p.Done);
                movements.Add(Move(product.Id, wh.Id, MovementType.ProductionReceipt, p.Done, po.ProductionNumber, receivedAt));
                if (p.Status == ProductionStatus.Completed) po.Complete(receivedAt);
                po.UpdatedAt = receivedAt;
            }
            else if (p.Status == ProductionStatus.Cancelled) po.Cancel();
            db.ProductionOrders.Add(po);
        }

        // ---- Final stock and opening balances ----
        var whCode = warehouses.Values.ToDictionary(w => w.Id, w => w.Code);
        var openingDate = monthStart.AddMonths(-12);
        foreach (var item in stock)
        {
            var product = products.First(p => p.Id == item.ProductId);
            var key = (item.ProductId, item.WarehouseId);
            var free = LowStockTargets.TryGetValue((product.ProductCode, whCode[item.WarehouseId]), out var target)
                ? target
                : Math.Round(product.MinimumStock * (1.4m + (decimal)rnd.NextDouble() * 2.2m), 0);
            item.QuantityReserved = reserved.GetValueOrDefault(key);
            item.QuantityAvailable = free + item.QuantityReserved;
            var opening = item.QuantityAvailable + shipped.GetValueOrDefault(key) - produced.GetValueOrDefault(key);
            if (opening < 0) { item.QuantityAvailable -= opening; opening = 0; }
            item.UpdatedAt = now;
            if (opening > 0)
                movements.Add(Move(item.ProductId, item.WarehouseId, MovementType.Import, opening, "SALDO-INICIAL", openingDate, "Saldo inicial migrado del sistema anterior"));
        }

        db.InventoryMovements.AddRange(movements);
        db.AuditLogs.Add(new AuditLog { Timestamp = now, UserName = SeedUser, Action = "DatabaseSeeded", EntityType = "System",
            Details = $"{orders.Count} sales orders, {plan.Length} production orders, {movements.Count} movements", Source = "APP" });
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Orders} sales orders, {Production} production orders and {Movements} movements",
            orders.Count, plan.Length, movements.Count);
    }

    private static InventoryMovement Move(int productId, int warehouseId, MovementType type, decimal qty, string reference, DateTime at, string? notes = null) =>
        new() { ProductId = productId, WarehouseId = warehouseId, MovementType = type, Quantity = qty, Reference = reference, Timestamp = at, UserName = SeedUser, Notes = notes };

    private static void Add(Dictionary<(int, int), decimal> map, (int, int) key, decimal qty) => map[key] = map.GetValueOrDefault(key) + qty;
}
