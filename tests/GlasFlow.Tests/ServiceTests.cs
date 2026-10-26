using System.Text;
using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GlasFlow.Tests;

public class SalesWorkflowTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    private CreateSalesOrderRequest Order(int customerId, int warehouseId, params (int Product, decimal Qty)[] lines) => new()
    {
        CustomerId = customerId,
        WarehouseId = warehouseId,
        Lines = lines.Select(l => new SalesOrderLineRequest { ProductId = l.Product, Quantity = l.Qty }).ToList()
    };

    [Fact]
    public async Task Create_calculates_totals_server_side_and_numbers_orders()
    {
        var o = await _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Tempered.Id, 10), (_t.Silicone.Id, 4)), default);
        Assert.Equal("SO-2026-0001", o.OrderNumber);
        Assert.Equal("Draft", o.Status);
        Assert.Equal(15080m, o.Subtotal);   // 14,500 + 580
        Assert.Equal(2412.80m, o.Tax);
        Assert.Equal(17492.80m, o.Total);

        var second = await _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Silicone.Id, 1)), default);
        Assert.Equal("SO-2026-0002", second.OrderNumber);
    }

    [Fact]
    public async Task Border_customer_is_taxed_at_eight_percent()
    {
        var o = await _t.Sales.CreateAsync(Order(_t.BorderCustomer.Id, _t.Border.Id, (_t.Tempered.Id, 2)), default);
        Assert.Equal(2900m, o.Subtotal);
        Assert.Equal(232m, o.Tax);
    }

    [Fact]
    public async Task Missing_customer_or_product_is_reported()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _t.Sales.CreateAsync(Order(999, _t.Main.Id, (_t.Tempered.Id, 1)), default));
        await Assert.ThrowsAsync<NotFoundException>(() => _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (999, 1)), default));
    }

    [Fact]
    public async Task Duplicate_external_reference_is_rejected()
    {
        var r = Order(_t.Customer.Id, _t.Main.Id, (_t.Tempered.Id, 1));
        r.ExternalReference = "ERP-PO-778";
        await _t.Sales.CreateAsync(r, default);
        await Assert.ThrowsAsync<ConflictException>(() => _t.Sales.CreateAsync(r, default));
    }

    [Fact]
    public async Task Duplicate_product_lines_are_rejected()
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Tempered.Id, 1), (_t.Tempered.Id, 2)), default));
    }

    [Fact]
    public async Task Confirm_reserves_ship_deducts_and_movements_are_recorded()
    {
        var o = await _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Tempered.Id, 40)), default);

        await _t.Sales.ConfirmAsync(o.Id, default);
        var afterConfirm = _t.Stock(_t.Tempered.Id, _t.Main.Id);
        Assert.Equal(100, afterConfirm.QuantityAvailable);
        Assert.Equal(40, afterConfirm.QuantityReserved);

        var shipped = await _t.Sales.ShipAsync(o.Id, default);
        Assert.Equal("Shipped", shipped.Status);
        var afterShip = _t.Stock(_t.Tempered.Id, _t.Main.Id);
        Assert.Equal(60, afterShip.QuantityAvailable);
        Assert.Equal(0, afterShip.QuantityReserved);

        var moves = await _t.Db.InventoryMovements.Where(m => m.Reference == o.OrderNumber).OrderBy(m => m.Id).ToListAsync();
        Assert.Equal([MovementType.Reservation, MovementType.SaleShipment], moves.Select(m => m.MovementType));
        Assert.Equal(-40, moves[1].Quantity);
        Assert.True(await _t.Db.AuditLogs.AnyAsync(a => a.Action == "SalesOrderShipped" && a.UserName == "tester"));
    }

    [Fact]
    public async Task Insufficient_stock_fails_atomically_without_partial_reservations()
    {
        // Laminated has 30; the first line fits, the second does not -> nothing may be reserved.
        var o = await _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Tempered.Id, 10), (_t.Laminated.Id, 31)), default);
        var ex = await Assert.ThrowsAsync<DomainException>(() => _t.Sales.ConfirmAsync(o.Id, default));
        Assert.Contains("LAM-6MM", ex.Message);

        _t.Db.ChangeTracker.Clear();
        Assert.Equal(0, _t.Stock(_t.Tempered.Id, _t.Main.Id).QuantityReserved);
        Assert.Equal("Draft", (await _t.Sales.GetAsync(o.Id, default)).Status);
        Assert.False(await _t.Db.InventoryMovements.AnyAsync(m => m.Reference == o.OrderNumber));
    }

    [Fact]
    public async Task Negative_stock_can_be_explicitly_allowed_by_configuration()
    {
        _t.InventoryOptions.AllowNegativeStock = true;
        var o = await _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Laminated.Id, 31)), default);
        await _t.Sales.ConfirmAsync(o.Id, default);
        var stock = _t.Stock(_t.Laminated.Id, _t.Main.Id);
        Assert.Equal(-1, stock.QuantityAvailable - stock.QuantityReserved);
    }

    [Fact]
    public async Task Cancelling_a_confirmed_order_releases_the_reservation()
    {
        var o = await _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Tempered.Id, 25)), default);
        await _t.Sales.ConfirmAsync(o.Id, default);
        await _t.Sales.CancelAsync(o.Id, default);
        Assert.Equal(0, _t.Stock(_t.Tempered.Id, _t.Main.Id).QuantityReserved);
        await Assert.ThrowsAsync<DomainException>(() => _t.Sales.ConfirmAsync(o.Id, default));
    }

    [Fact]
    public async Task Confirming_into_low_stock_raises_a_single_alert()
    {
        // Tempered: 100 on hand, minimum 50. Reserving 60 leaves 40 free -> low stock.
        var o = await _t.Sales.CreateAsync(Order(_t.Customer.Id, _t.Main.Id, (_t.Tempered.Id, 60)), default);
        await _t.Sales.ConfirmAsync(o.Id, default);
        var key = $"LOWSTOCK:{_t.Tempered.Id}:{_t.Main.Id}";
        Assert.Equal(1, await _t.Db.Notifications.CountAsync(n => n.DedupKey == key && !n.IsResolved));
    }
}

public class ProductionWorkflowTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Completing_production_updates_finished_goods_inventory()
    {
        var po = await _t.Production.CreateAsync(new CreateProductionOrderRequest { ProductId = _t.Laminated.Id, WarehouseId = _t.Main.Id, QuantityPlanned = 50 }, default);
        Assert.Equal("Planned", po.Status);
        await _t.Production.StartAsync(po.Id, default);
        await _t.Production.RegisterAsync(po.Id, 30, default);
        Assert.Equal(60, _t.Stock(_t.Laminated.Id, _t.Main.Id).QuantityAvailable);

        var done = await _t.Production.CompleteAsync(po.Id, 20, default);
        Assert.Equal("Completed", done.Status);
        Assert.Equal(50, done.QuantityCompleted);
        Assert.Equal(100m, done.ProgressPercent);
        Assert.Equal(80, _t.Stock(_t.Laminated.Id, _t.Main.Id).QuantityAvailable);

        var receipts = await _t.Db.InventoryMovements.Where(m => m.Reference == po.ProductionNumber).ToListAsync();
        Assert.Equal(2, receipts.Count);
        Assert.All(receipts, m => Assert.Equal(MovementType.ProductionReceipt, m.MovementType));
        Assert.True(await _t.Db.Notifications.AnyAsync(n => n.Type == NotificationType.ProductionCompleted));
    }

    [Fact]
    public async Task Over_production_is_rejected_and_leaves_stock_unchanged()
    {
        var po = await _t.Production.CreateAsync(new CreateProductionOrderRequest { ProductId = _t.Tempered.Id, WarehouseId = _t.Main.Id, QuantityPlanned = 10 }, default);
        await _t.Production.StartAsync(po.Id, default);
        await Assert.ThrowsAsync<DomainException>(() => _t.Production.RegisterAsync(po.Id, 11, default));
        Assert.Equal(100, _t.Stock(_t.Tempered.Id, _t.Main.Id).QuantityAvailable);
    }

    [Fact]
    public async Task Accessories_cannot_be_produced()
    {
        await Assert.ThrowsAsync<DomainException>(() => _t.Production.CreateAsync(
            new CreateProductionOrderRequest { ProductId = _t.Silicone.Id, WarehouseId = _t.Main.Id, QuantityPlanned = 5 }, default));
    }
}

public class InventoryAutomationTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Low_stock_check_is_idempotent_and_resolves_recovered_items()
    {
        // Tempered in the border warehouse: 10 on hand vs minimum 50 -> low.
        var first = await _t.Alerts.RunFullCheckAsync(default);
        var second = await _t.Alerts.RunFullCheckAsync(default);
        Assert.Equal(1, first.NotificationsCreated);
        Assert.Equal(0, second.NotificationsCreated);
        Assert.Equal(1, await _t.Db.Notifications.CountAsync(n => n.Type == NotificationType.LowStock));

        await _t.Inventory.AdjustAsync(new StockAdjustmentRequest { ProductId = _t.Tempered.Id, WarehouseId = _t.Border.Id, Quantity = 100, Reason = "Recepción de compra" }, default);
        var open = await _t.Db.Notifications.CountAsync(n => n.Type == NotificationType.LowStock && !n.IsResolved);
        Assert.Equal(0, open);
    }

    [Fact]
    public async Task Transfer_moves_stock_and_records_both_sides()
    {
        await _t.Inventory.TransferAsync(new StockTransferRequest { ProductId = _t.Tempered.Id, FromWarehouseId = _t.Main.Id, ToWarehouseId = _t.Border.Id, Quantity = 15 }, default);
        Assert.Equal(85, _t.Stock(_t.Tempered.Id, _t.Main.Id).QuantityAvailable);
        Assert.Equal(25, _t.Stock(_t.Tempered.Id, _t.Border.Id).QuantityAvailable);
        Assert.Equal(2, await _t.Db.InventoryMovements.CountAsync(m => m.MovementType == MovementType.TransferOut || m.MovementType == MovementType.TransferIn));
    }

    [Fact]
    public async Task Adjustment_below_zero_is_rejected()
    {
        await Assert.ThrowsAsync<DomainException>(() => _t.Inventory.AdjustAsync(
            new StockAdjustmentRequest { ProductId = _t.Laminated.Id, WarehouseId = _t.Main.Id, Quantity = -31, Reason = "Conteo" }, default));
    }

    [Fact]
    public async Task Duplicate_product_code_is_a_conflict()
    {
        var r = new ProductRequest { ProductCode = "tmp-10mm", ProductName = "Otro", Category = ProductCategories.Tempered, Unit = "m2", Cost = 1, SellingPrice = 2 };
        await Assert.ThrowsAsync<ConflictException>(() => _t.Products.CreateAsync(r, default));
    }

    [Fact]
    public async Task Selling_below_cost_is_rejected()
    {
        var r = new ProductRequest { ProductCode = "NEW-1", ProductName = "Nuevo", Category = ProductCategories.Clear, Unit = "m2", Cost = 100, SellingPrice = 90 };
        await Assert.ThrowsAsync<DomainException>(() => _t.Products.CreateAsync(r, default));
    }
}

public class ImportTests : IDisposable
{
    private readonly TestDb _t = new();
    public void Dispose() => _t.Dispose();

    private static MemoryStream File(string csv) => new(Encoding.UTF8.GetBytes(csv));

    private const string Csv =
        "erp_sku,code,name,familia,uom,costo,precio,min_stock\n" +
        "SKU-1,TMP-8MM,Templado 8 mm,TEMPERED,sqm,700,1100,30\n" +
        "SKU-2,,Espejo bronce,FROSTED,sqm,400,650,10\n" +
        "SKU-3,X-3,Sin categoría,PLASTIC,sqm,1,2,0\n" +
        "SKU-1,DUP,Duplicado,TEMPERED,sqm,1,2,0\n" +
        "SKU-4,X-4,Precio bajo,CLEAR,sqm,100,50,0\n";

    [Fact]
    public async Task Import_maps_identifiers_and_reports_row_errors()
    {
        var result = await _t.Import.ImportProductsAsync(File(Csv), dryRun: false, default);
        Assert.Equal(5, result.TotalRows);
        Assert.Equal(2, result.Created);
        Assert.Equal(3, result.Skipped);
        Assert.Contains(result.Errors, e => e.LineNumber == 4 && e.Message.Contains("category"));
        Assert.Contains(result.Errors, e => e.LineNumber == 5 && e.Message.Contains("Duplicate"));
        Assert.Contains(result.Errors, e => e.LineNumber == 6 && e.Message.Contains("lower than Cost"));

        var mirror = await _t.Db.Products.SingleAsync(p => p.ExternalId == "SKU-2");
        Assert.Equal("ERP-SKU-2", mirror.ProductCode);              // generated code for unmapped items
        Assert.Equal(ProductCategories.Decorative, mirror.Category); // FROSTED -> Vidrio decorativo
        Assert.Equal(2, await _t.Db.Inventory.CountAsync(i => i.ProductId == mirror.Id)); // stock row per warehouse
    }

    [Fact]
    public async Task Reimport_updates_instead_of_duplicating_and_dry_run_saves_nothing()
    {
        var dry = await _t.Import.ImportProductsAsync(File(Csv), dryRun: true, default);
        Assert.True(dry.DryRun);
        _t.Db.ChangeTracker.Clear();
        Assert.False(await _t.Db.Products.AnyAsync(p => p.ExternalId == "SKU-1"));

        await _t.Import.ImportProductsAsync(File(Csv), false, default);
        _t.Db.ChangeTracker.Clear();
        var again = await _t.Import.ImportProductsAsync(File(Csv.Replace("700,1100", "720,1150")), false, default);
        Assert.Equal(0, again.Created);
        Assert.Equal(2, again.Updated);
        Assert.Equal(1150m, (await _t.Db.Products.SingleAsync(p => p.ExternalId == "SKU-1")).SellingPrice);
    }

    [Fact]
    public async Task Missing_required_columns_fail_fast()
    {
        await Assert.ThrowsAsync<DomainException>(() => _t.Import.ImportProductsAsync(File("name,price\nA,1\n"), false, default));
    }
}
