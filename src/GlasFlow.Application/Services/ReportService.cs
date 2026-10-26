using System.Globalization;
using System.Reflection;
using System.Text;
using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GlasFlow.Application.Services;

/// <summary>
/// Report datasets. The same columns are exposed by the SQL Server views in database/04_views.sql
/// (vw_SalesReport, vw_CurrentInventory, vw_ProductionStatus) so Crystal Reports can bind to either.
/// </summary>
public class ReportService(IGlasFlowDbContext db, TimeProvider clock)
{
    public async Task<ReportResult<SalesReportRow>> SalesAsync(DateTime? from, DateTime? to, string? status, CancellationToken ct)
    {
        var q = db.SalesOrders.AsNoTracking().Include(o => o.Customer).AsQueryable();
        if (from is not null) q = q.Where(o => o.OrderDate >= from);
        if (to is not null) q = q.Where(o => o.OrderDate < to.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SalesOrderStatus>(status, true, out var s)) q = q.Where(o => o.Status == s);
        var rows = (await q.OrderBy(o => o.OrderDate).ToListAsync(ct))
            .Select(o => new SalesReportRow(o.OrderNumber, o.OrderDate, o.Customer!.CustomerCode, o.Customer.Name,
                o.Status.ToString(), o.Subtotal, o.Tax, o.Total)).ToList();
        var billed = rows.Where(r => r.Status is "Confirmed" or "Shipped").ToList();
        return new ReportResult<SalesReportRow>("Reporte de ventas", Now, from, to, rows, new Dictionary<string, decimal>
        {
            ["Orders"] = rows.Count,
            ["BilledSubtotal"] = billed.Sum(r => r.Subtotal),
            ["BilledTax"] = billed.Sum(r => r.Tax),
            ["BilledTotal"] = billed.Sum(r => r.Total)
        });
    }

    public async Task<ReportResult<InventoryReportRow>> InventoryAsync(int? warehouseId, string? category, bool lowStockOnly, CancellationToken ct)
    {
        var q = db.Inventory.AsNoTracking().Include(i => i.Product).Include(i => i.Warehouse).Where(i => i.Product!.Active);
        if (warehouseId is not null) q = q.Where(i => i.WarehouseId == warehouseId);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(i => i.Product!.Category == category);
        var items = await q.OrderBy(i => i.Product!.Category).ThenBy(i => i.Product!.ProductCode).ThenBy(i => i.Warehouse!.Code).ToListAsync(ct);
        var rows = items.Select(i => new InventoryReportRow(i.Product!.ProductCode, i.Product.ProductName, i.Product.Category,
                i.Warehouse!.Code, i.Product.Unit, i.QuantityAvailable, i.QuantityReserved, i.QuantityFree, i.Product.MinimumStock,
                i.Product.Cost, Money.Round(i.QuantityAvailable * i.Product.Cost), i.IsLowStock(i.Product.MinimumStock)))
            .Where(r => !lowStockOnly || r.IsLowStock).ToList();
        return new ReportResult<InventoryReportRow>(lowStockOnly ? "Reporte de inventario bajo" : "Reporte de inventario", Now, null, null, rows,
            new Dictionary<string, decimal>
            {
                ["Lines"] = rows.Count,
                ["LowStockLines"] = rows.Count(r => r.IsLowStock),
                ["InventoryValue"] = rows.Sum(r => r.StockValue)
            });
    }

    public async Task<ReportResult<ProductionReportRow>> ProductionAsync(DateTime? from, DateTime? to, string? status, CancellationToken ct)
    {
        var q = db.ProductionOrders.AsNoTracking().Include(p => p.Product).AsQueryable();
        if (from is not null) q = q.Where(p => p.CreatedAt >= from);
        if (to is not null) q = q.Where(p => p.CreatedAt < to.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ProductionStatus>(status, true, out var s)) q = q.Where(p => p.Status == s);
        var rows = (await q.OrderBy(p => p.ProductionNumber).ToListAsync(ct))
            .Select(p => new ProductionReportRow(p.ProductionNumber, p.Product!.ProductCode, p.Product.ProductName, p.Status.ToString(),
                p.QuantityPlanned, p.QuantityCompleted, p.ProgressPercent, p.StartDate, p.CompletionDate, p.DueDate)).ToList();
        return new ReportResult<ProductionReportRow>("Reporte de producción", Now, from, to, rows, new Dictionary<string, decimal>
        {
            ["Orders"] = rows.Count,
            ["Planned"] = rows.Sum(r => r.QuantityPlanned),
            ["Completed"] = rows.Sum(r => r.QuantityCompleted)
        });
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
}

/// <summary>Minimal RFC 4180 CSV writer/reader (no external dependency).</summary>
public static class Csv
{
    public static byte[] Write<T>(IEnumerable<T> rows)
    {
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(',', props.Select(p => Escape(p.Name))));
        foreach (var row in rows)
            sb.AppendLine(string.Join(',', props.Select(p => Escape(Format(p.GetValue(row))))));
        // UTF-8 BOM so Excel opens accented Spanish text correctly.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static List<string[]> Read(TextReader reader)
    {
        var rows = new List<string[]>();
        var field = new StringBuilder();
        var current = new List<string>();
        bool inQuotes = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"') { field.Append('"'); reader.Read(); }
                    else inQuotes = false;
                }
                else field.Append(ch);
            }
            else if (ch == '"') inQuotes = true;
            else if (ch == ',') { current.Add(field.ToString()); field.Clear(); }
            else if (ch == '\n' || ch == '\r')
            {
                if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                current.Add(field.ToString()); field.Clear();
                if (current.Count > 1 || current[0].Length > 0) rows.Add([.. current]);
                current = [];
            }
            else field.Append(ch);
        }
        if (field.Length > 0 || current.Count > 0) { current.Add(field.ToString()); rows.Add([.. current]); }
        return rows;
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        decimal m => m.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    private static string Escape(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
