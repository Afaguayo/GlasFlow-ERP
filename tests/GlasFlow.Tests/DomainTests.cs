using GlasFlow.Application.Services;
using GlasFlow.Domain.Entities;

namespace GlasFlow.Tests;

public class TaxTests
{
    [Theory]
    [InlineData(TaxZone.General, 1000, 160)]
    [InlineData(TaxZone.FronteraNorte, 1000, 80)]
    [InlineData(TaxZone.General, 0.03, 0)]          // 0.0048 rounds to 0.00
    [InlineData(TaxZone.General, 1234.56, 197.53)]  // 197.5296 -> 197.53
    public void Iva_is_calculated_per_zone_and_rounded_to_cents(TaxZone zone, decimal subtotal, decimal expected) =>
        Assert.Equal(expected, TaxCalculator.TaxFor(subtotal, zone));

    [Fact]
    public void Negative_subtotal_is_rejected() =>
        Assert.Throws<DomainException>(() => TaxCalculator.TaxFor(-1, TaxZone.General));
}

public class SalesOrderDomainTests
{
    private static Product Glass(decimal price = 1450) => new()
    {
        Id = 1, ProductCode = "TMP-10MM", ProductName = "Templado", Category = ProductCategories.Tempered, SellingPrice = price, Active = true
    };

    [Fact]
    public void Totals_are_line_sum_plus_iva()
    {
        var order = new SalesOrder { OrderNumber = "SO-1" };
        order.AddLine(Glass(), 12.5m);                       // 18,125.00
        order.AddLine(new Product { Id = 2, ProductCode = "ACC", Active = true, SellingPrice = 145 }, 3); // 435.00
        order.RecalculateTotals(TaxZone.General);

        Assert.Equal(18560.00m, order.Subtotal);
        Assert.Equal(2969.60m, order.Tax);
        Assert.Equal(21529.60m, order.Total);
        Assert.Equal(0.16m, order.TaxRate);
    }

    [Fact]
    public void Border_zone_uses_eight_percent()
    {
        var order = new SalesOrder();
        order.AddLine(Glass(1000), 1);
        order.RecalculateTotals(TaxZone.FronteraNorte);
        Assert.Equal(80m, order.Tax);
        Assert.Equal(1080m, order.Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Invalid_quantities_are_rejected(decimal qty) =>
        Assert.Throws<DomainException>(() => new SalesOrder().AddLine(Glass(), qty));

    [Fact]
    public void Inactive_product_cannot_be_sold()
    {
        var p = Glass();
        p.Active = false;
        Assert.Throws<DomainException>(() => new SalesOrder().AddLine(p, 1));
    }

    [Fact]
    public void Status_transitions_follow_the_workflow()
    {
        var order = new SalesOrder { OrderNumber = "SO-1" };
        Assert.Throws<DomainException>(() => order.MarkConfirmed(DateTime.UtcNow)); // no lines
        order.AddLine(Glass(), 1);
        Assert.Throws<DomainException>(() => order.MarkShipped(DateTime.UtcNow));   // not confirmed yet
        order.MarkConfirmed(DateTime.UtcNow);
        Assert.Throws<DomainException>(() => order.AddLine(Glass(), 1));            // lines frozen after confirm
        order.MarkShipped(DateTime.UtcNow);
        Assert.Equal(SalesOrderStatus.Shipped, order.Status);
        Assert.Throws<DomainException>(() => order.MarkCancelled());                // shipped is final
    }
}

public class InventoryDomainTests
{
    [Fact]
    public void Reserve_reduces_free_stock_but_not_on_hand()
    {
        var item = new InventoryItem { QuantityAvailable = 100 };
        item.Reserve(30);
        Assert.Equal(100, item.QuantityAvailable);
        Assert.Equal(70, item.QuantityFree);
        Assert.Equal(1, item.Version);
    }

    [Fact]
    public void Reserve_more_than_free_stock_fails_unless_negative_allowed()
    {
        var item = new InventoryItem { QuantityAvailable = 10, QuantityReserved = 5 };
        Assert.Throws<DomainException>(() => item.Reserve(6));
        item.Reserve(6, allowNegative: true);
        Assert.Equal(-1, item.QuantityFree);
    }

    [Fact]
    public void Shipping_deducts_reserved_and_on_hand()
    {
        var item = new InventoryItem { QuantityAvailable = 50, QuantityReserved = 20 };
        item.ShipReserved(20);
        Assert.Equal(30, item.QuantityAvailable);
        Assert.Equal(0, item.QuantityReserved);
        Assert.Throws<DomainException>(() => item.ShipReserved(1));
    }

    [Fact]
    public void Adjustment_cannot_go_negative_or_below_reserved()
    {
        var item = new InventoryItem { QuantityAvailable = 10, QuantityReserved = 4 };
        Assert.Throws<DomainException>(() => item.Adjust(-11));
        Assert.Throws<DomainException>(() => item.Adjust(-7));
        Assert.Throws<DomainException>(() => item.Adjust(0));
        item.Adjust(-6);
        Assert.Equal(4, item.QuantityAvailable);
    }

    [Theory]
    [InlineData(100, 0, 50, false)]
    [InlineData(50, 0, 50, true)]   // at the minimum counts as low
    [InlineData(80, 40, 50, true)]  // reservations reduce free stock
    [InlineData(0, 0, 0, false)]    // minimum 0 = not tracked
    public void Low_stock_uses_free_quantity(decimal available, decimal reserved, decimal minimum, bool expected) =>
        Assert.Equal(expected, new InventoryItem { QuantityAvailable = available, QuantityReserved = reserved }.IsLowStock(minimum));
}

public class ProductionDomainTests
{
    [Fact]
    public void Production_lifecycle_and_limits()
    {
        var po = new ProductionOrder { QuantityPlanned = 100 };
        Assert.Throws<DomainException>(() => po.RegisterCompleted(10)); // not started
        po.Start(DateTime.UtcNow);
        po.RegisterCompleted(60);
        Assert.Equal(60m, po.ProgressPercent);
        Assert.Throws<DomainException>(() => po.RegisterCompleted(41)); // exceeds plan
        Assert.Throws<DomainException>(() => po.RegisterCompleted(0));
        po.Complete(DateTime.UtcNow);
        Assert.Equal(ProductionStatus.Completed, po.Status);
        Assert.NotNull(po.CompletionDate);
        Assert.Throws<DomainException>(() => po.Cancel());
    }

    [Fact]
    public void Cannot_complete_without_registered_units()
    {
        var po = new ProductionOrder { QuantityPlanned = 10 };
        po.Start(DateTime.UtcNow);
        Assert.Throws<DomainException>(() => po.Complete(DateTime.UtcNow));
    }
}

public class ProductValidationTests
{
    private static Product Valid() => new() { ProductCode = "TMP-6MM", ProductName = "Templado 6 mm", Category = ProductCategories.Tempered, Cost = 600, SellingPrice = 980 };

    [Fact]
    public void Valid_product_passes() => Valid().Validate();

    [Theory]
    [InlineData("", "Templado", ProductCategories.Tempered, 1, 10)]
    [InlineData("X1", "", ProductCategories.Tempered, 1, 10)]
    [InlineData("X1", "Templado", "Plástico", 1, 10)]
    [InlineData("X1", "Templado", ProductCategories.Tempered, -1, 10)]
    [InlineData("X1", "Templado", ProductCategories.Tempered, 1, 0)]
    public void Invalid_product_data_is_rejected(string code, string name, string category, decimal cost, decimal price)
    {
        var p = Valid();
        p.ProductCode = code; p.ProductName = name; p.Category = category; p.Cost = cost; p.SellingPrice = price;
        Assert.Throws<DomainException>(p.Validate);
    }
}

public class AssistantTests
{
    [Theory]
    [InlineData("¿Qué productos tienen inventario bajo?", AssistantIntent.LowStock)]
    [InlineData("Resume las ventas del mes.", AssistantIntent.SalesSummary)]
    [InlineData("¿Qué órdenes de producción están pendientes?", AssistantIntent.ProductionStatus)]
    [InlineData("¿Qué productos tienen mayor movimiento?", AssistantIntent.TopMovers)]
    [InlineData("¿Qué debería revisar el administrador hoy?", AssistantIntent.DailyBriefing)]
    public void Suggested_questions_map_to_intents(string question, AssistantIntent intent) =>
        Assert.Equal(intent, AssistantService.DetectIntent(question));

    [Fact]
    public void Local_summary_explains_low_stock_with_production_context()
    {
        const string json = """[{"ProductCode":"LAM-ACU","ProductName":"Laminado acústico","Warehouse":"CJS-01","Unit":"m2","Free":0,"Reserved":0,"Minimum":20,"Shortfall":20,"PendingProduction":60}]""";
        var text = AssistantService.LocalSummary(AssistantIntent.LowStock, json);
        Assert.Contains("LAM-ACU", text);
        Assert.Contains("mínimo 20", text);
        Assert.Contains("60 en producción", text);
    }
}

public class CsvTests
{
    private record Row(string Name, decimal Amount, string Note);

    [Fact]
    public void Write_then_read_round_trips_quotes_commas_and_accents()
    {
        var bytes = Csv.Write([new Row("Vidrio \"claro\", 6 mm", 1234.5m, "línea\nnueva")]);
        using var reader = new StreamReader(new MemoryStream(bytes));
        var rows = Csv.Read(reader);
        Assert.Equal(2, rows.Count);
        Assert.Equal(["Name", "Amount", "Note"], rows[0].Select(h => h.TrimStart('﻿')).ToArray());
        Assert.Equal("Vidrio \"claro\", 6 mm", rows[1][0]);
        Assert.Equal("1234.5", rows[1][1]);
        Assert.Equal("línea\nnueva", rows[1][2]);
    }
}
