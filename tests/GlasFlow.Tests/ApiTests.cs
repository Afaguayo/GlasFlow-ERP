using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace GlasFlow.Tests;

/// <summary>
/// Boots the real API (all middleware, auth, validation, EF Core) on a throw-away SQLite file database,
/// seeded with the demo data set.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test-Password-123!";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"glasflow-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("ConnectionStrings:GlasFlowSqlite", $"Data Source={_dbPath};Pooling=False");
        builder.UseSetting("Auth:DemoPassword", Password);
        builder.UseSetting("Automation:InventoryCheckMinutes", "0");
        builder.UseSetting("RateLimits:LoginPerMinute", "1000");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { File.Delete(_dbPath); } catch (IOException) { }
    }
}

public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private async Task<HttpClient> ClientAs(string user)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = user, password = ApiFactory.Password });
        login.EnsureSuccessStatusCode();
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> Body(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>(Json);

    [Fact]
    public async Task Health_is_public_and_healthy()
    {
        var r = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Healthy", (await Body(r)).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("/swagger/index.html")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/")]
    public async Task Docs_and_web_ui_are_public(string path)
    {
        var r = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Api_requires_authentication()
    {
        var r = await factory.CreateClient().GetAsync("/api/products");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_is_rejected()
    {
        var r = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "nope-nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Viewer_can_read_but_not_write()
    {
        var viewer = await ClientAs("consulta");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/dashboard")).StatusCode);
        var write = await viewer.PostAsync("/api/inventory/check", null);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Invalid_product_returns_validation_problem_details()
    {
        var admin = await ClientAs("admin");
        var r = await admin.PostAsJsonAsync("/api/products", new { productCode = "X", productName = "", category = "Vidrio templado", unit = "m3", sellingPrice = -5 });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        var errors = (await Body(r)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("Unit", out _));
        Assert.True(errors.TryGetProperty("SellingPrice", out _));
    }

    [Fact]
    public async Task Unknown_order_returns_404_problem()
    {
        var admin = await ClientAs("admin");
        var r = await admin.GetAsync("/api/sales-orders/999999");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Contains("999999", (await Body(r)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Sales_order_flow_over_http()
    {
        var op = await ClientAs("operador");
        var products = await Body(await op.GetAsync("/api/products?search=TMP-10MM"));
        var productId = products.GetProperty("items")[0].GetProperty("id").GetInt32();
        var customers = await Body(await op.GetAsync("/api/customers?search=CLI-001"));
        var customerId = customers.GetProperty("items")[0].GetProperty("id").GetInt32();
        var warehouses = await Body(await op.GetAsync("/api/warehouses"));
        var warehouseId = warehouses.EnumerateArray().First(w => w.GetProperty("code").GetString() == "CUU-01").GetProperty("id").GetInt32();

        var create = await op.PostAsJsonAsync("/api/sales-orders", new { customerId, warehouseId, externalReference = "IT-1", lines = new[] { new { productId, quantity = 2 } } });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var order = await Body(create);
        var id = order.GetProperty("id").GetInt32();
        Assert.Equal(2900m, order.GetProperty("subtotal").GetDecimal());
        Assert.Equal(464m, order.GetProperty("tax").GetDecimal());

        var duplicate = await op.PostAsJsonAsync("/api/sales-orders", new { customerId, warehouseId, externalReference = "IT-1", lines = new[] { new { productId, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        Assert.Equal("Confirmed", (await Body(await op.PostAsync($"/api/sales-orders/{id}/confirm", null))).GetProperty("status").GetString());
        Assert.Equal("Shipped", (await Body(await op.PostAsync($"/api/sales-orders/{id}/ship", null))).GetProperty("status").GetString());
        var again = await op.PostAsync($"/api/sales-orders/{id}/ship", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, again.StatusCode);
    }

    [Fact]
    public async Task Missing_customer_returns_404()
    {
        var op = await ClientAs("operador");
        var r = await op.PostAsJsonAsync("/api/sales-orders", new { customerId = 99999, warehouseId = 1, lines = new[] { new { productId = 1, quantity = 1 } } });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    [Fact]
    public async Task Seeded_data_feeds_dashboard_reports_and_assistant()
    {
        var admin = await ClientAs("admin");
        var dash = await Body(await admin.GetAsync("/api/dashboard"));
        Assert.True(dash.GetProperty("kpis").GetProperty("salesYearToDate").GetDecimal() > 0);
        Assert.Equal(12, dash.GetProperty("salesByMonth").GetArrayLength());
        Assert.True(dash.GetProperty("kpis").GetProperty("lowStockProducts").GetInt32() > 0);

        var csv = await admin.GetAsync("/api/reports/inventory/csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("ProductCode,", (await csv.Content.ReadAsStringAsync()).TrimStart('﻿'));

        var answer = await Body(await admin.PostAsJsonAsync("/api/assistant/ask", new { question = "¿Qué productos tienen inventario bajo?" }));
        Assert.Equal("LowStock", answer.GetProperty("intent").GetString());
        Assert.True(answer.GetProperty("usedFallback").GetBoolean()); // no AI key in tests
        Assert.Contains("inventario bajo", answer.GetProperty("answer").GetString());
    }

    [Fact]
    public async Task Assistant_rejects_invalid_questions()
    {
        var admin = await ClientAs("admin");
        var r = await admin.PostAsJsonAsync("/api/assistant/ask", new { question = "a" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }
}
