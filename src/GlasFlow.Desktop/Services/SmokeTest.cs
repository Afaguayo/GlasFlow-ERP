using System.Text;

namespace GlasFlow.Desktop.Services;

/// <summary>
/// Headless check (--smoke): exercises the same ApiClient the UI uses and writes smoke.log next to the exe.
/// Credentials come from GLASFLOW_USER / GLASFLOW_PASSWORD. Returns the process exit code (0 = OK, 1 = failure).
/// </summary>
public static class SmokeTest
{
    public static async Task<int> RunAsync(ApiClient api)
    {
        var log = new StringBuilder();
        void Line(string s) => log.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}");
        var failed = false;

        async Task Step(string name, Func<Task<string>> action)
        {
            try { Line($"OK   {name}: {await action()}"); }
            catch (Exception ex) { failed = true; Line($"FAIL {name}: {ex.Message.Replace('\n', ' ')}"); }
        }

        Line($"GlasFlow.Desktop smoke test against {api.BaseUrl}");
        var user = Environment.GetEnvironmentVariable("GLASFLOW_USER");
        var password = Environment.GetEnvironmentVariable("GLASFLOW_PASSWORD");

        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
        {
            Line("FAIL login: GLASFLOW_USER and GLASFLOW_PASSWORD must be set.");
            failed = true;
        }
        else
        {
            await Step("login", async () =>
            {
                var s = await api.LoginAsync(user, password);
                return $"user={s.Username} role={s.Role} tokenLength={s.Token.Length}";
            });

            if (!failed)
            {
                await Step("dashboard", async () =>
                {
                    var d = await api.GetDashboardAsync();
                    return $"salesThisMonth={d.Kpis.SalesThisMonth:N2} pendingOrders={d.Kpis.PendingOrders} lowStock={d.Kpis.LowStockProducts} " +
                           $"inventoryValue={d.Kpis.InventoryValue:N2} months={d.SalesByMonth.Count} recentOrders={d.RecentOrders.Count}";
                });
                await Step("categories", async () => $"{(await api.GetCategoriesAsync()).Count} categories");
                await Step("warehouses", async () => $"{(await api.GetWarehousesAsync()).Count} warehouses");
                await Step("suppliers", async () => $"{(await api.GetSuppliersAsync()).Count} suppliers");
                await Step("inventory", async () =>
                {
                    var r = await api.GetInventoryAsync(null, null, null, false, 1, 50);
                    return $"{r.Items.Count} items on page {r.Page}, total {r.TotalCount}";
                });
                await Step("inventory(lowStockOnly)", async () =>
                {
                    var r = await api.GetInventoryAsync(null, null, null, true, 1, 50);
                    return $"total {r.TotalCount}";
                });
                await Step("products", async () =>
                {
                    var r = await api.GetProductsAsync(null, 1, 25);
                    return $"{r.Items.Count} items on page {r.Page}, total {r.TotalCount}";
                });
                await Step("sales-orders", async () =>
                {
                    var r = await api.GetSalesOrdersAsync(null, null, 1, 25);
                    var detail = r.Items.Count > 0 ? await api.GetSalesOrderAsync(r.Items[0].Id) : null;
                    return $"{r.Items.Count} items, total {r.TotalCount}" +
                           (detail is null ? "" : $"; first {detail.OrderNumber} {detail.Status} lines={detail.Lines.Count} total={detail.Total:N2}");
                });
                await Step("error mapping (invalid order)", async () =>
                {
                    try { await api.CreateSalesOrderAsync(new CreateSalesOrderRequest(0, 0, null, null, [])); }
                    catch (ApiException ex) { return $"server rejected as expected -> \"{ex.Message.Replace('\n', ' ')}\""; }
                    throw new ApiException("server accepted an invalid order");
                });
            }
        }

        Line(failed ? "RESULT: FAILED" : "RESULT: PASSED");
        try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke.log"), log.ToString(), new UTF8Encoding(false)); }
        catch { /* nothing else to do in a WinExe */ }
        return failed ? 1 : 0;
    }
}
