using System.Globalization;
using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GlasFlow.Application.Services;

public class DashboardService(IGlasFlowDbContext db, TimeProvider clock)
{
    private static readonly CultureInfo Es = CultureInfo.GetCultureInfo("es-MX");
    private static readonly SalesOrderStatus[] Billed = [SalesOrderStatus.Confirmed, SalesOrderStatus.Shipped];

    public async Task<DashboardDto> GetAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var lastMonthStart = monthStart.AddMonths(-1);
        var yearStart = new DateTime(now.Year, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var billed = db.SalesOrders.AsNoTracking().Where(o => Billed.Contains(o.Status));
        var salesThisMonth = await billed.Where(o => o.OrderDate >= monthStart).SumAsync(o => o.Total, ct);
        var salesLastMonth = await billed.Where(o => o.OrderDate >= lastMonthStart && o.OrderDate < monthStart).SumAsync(o => o.Total, ct);
        var salesYtd = await billed.Where(o => o.OrderDate >= yearStart).SumAsync(o => o.Total, ct);
        var pending = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Draft, ct);
        var confirmed = await db.SalesOrders.CountAsync(o => o.Status == SalesOrderStatus.Confirmed, ct);

        var stock = await db.Inventory.AsNoTracking().Include(i => i.Product).Include(i => i.Warehouse)
            .Where(i => i.Product!.Active).ToListAsync(ct);
        var inventoryValue = Money.Round(stock.Sum(i => i.QuantityAvailable * i.Product!.Cost));
        var lowStock = stock.Where(i => i.IsLowStock(i.Product!.MinimumStock)).OrderBy(i => i.QuantityFree - i.Product!.MinimumStock).ToList();

        var active = await db.ProductionOrders.AsNoTracking().Include(p => p.Product).Include(p => p.Warehouse)
            .Where(p => p.Status == ProductionStatus.Planned || p.Status == ProductionStatus.InProgress)
            .OrderBy(p => p.DueDate).ToListAsync(ct);
        var planned = active.Sum(p => p.QuantityPlanned);
        var progress = planned == 0 ? 0 : Math.Round(active.Sum(p => p.QuantityCompleted) / planned * 100, 1);

        // Twelve-month window, grouped in memory so it is provider-neutral; the SQL Server view vw_SalesByMonth is the server-side equivalent.
        var windowStart = monthStart.AddMonths(-11);
        var orders = await billed.Where(o => o.OrderDate >= windowStart).Select(o => new { o.OrderDate, o.Total }).ToListAsync(ct);
        var byMonth = Enumerable.Range(0, 12).Select(i => windowStart.AddMonths(i)).Select(m =>
        {
            var inMonth = orders.Where(o => o.OrderDate.Year == m.Year && o.OrderDate.Month == m.Month).ToList();
            return new MonthlySalesDto(m.Year, m.Month, Es.TextInfo.ToTitleCase(m.ToString("MMM yy", Es)), inMonth.Sum(o => o.Total), inMonth.Count);
        }).ToList();

        var byCategory = stock.GroupBy(i => i.Product!.Category)
            .Select(g => new CategoryValueDto(g.Key, Money.Round(g.Sum(i => i.QuantityAvailable * i.Product!.Cost))))
            .OrderByDescending(c => c.Value).ToList();

        var movements = await db.InventoryMovements.AsNoTracking().Include(m => m.Product).Include(m => m.Warehouse)
            .OrderByDescending(m => m.Timestamp).ThenByDescending(m => m.Id).Take(10).ToListAsync(ct);
        var recentOrders = await db.SalesOrders.AsNoTracking().Include(o => o.Customer).Include(o => o.Warehouse).Include(o => o.Details)
            .OrderByDescending(o => o.OrderDate).Take(8).ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(n => !n.IsRead && !n.IsResolved, ct);

        var kpis = new KpiDto(salesThisMonth, salesLastMonth, salesYtd, pending, confirmed, inventoryValue,
            lowStock.Select(i => i.ProductId).Distinct().Count(), active.Count, progress, unread);
        return new DashboardDto(kpis, byMonth, byCategory, movements.Select(m => m.ToDto()).ToList(),
            lowStock.Take(10).Select(i => i.ToDto()).ToList(), active.Take(8).Select(p => p.ToDto()).ToList(),
            recentOrders.Select(o => o.ToSummary()).ToList(), now);
    }
}
