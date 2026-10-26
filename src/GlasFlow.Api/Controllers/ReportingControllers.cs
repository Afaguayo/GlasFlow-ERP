using System.Data;
using System.Reflection;
using GlasFlow.Api.Infrastructure;
using GlasFlow.Application.Dtos;
using GlasFlow.Application.Services;
using GlasFlow.Domain.Entities;
using GlasFlow.Infrastructure.Ai;
using GlasFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace GlasFlow.Api.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController(ReportService reports, GlasFlowDbContext db) : ControllerBase
{
    [HttpGet("sales")]
    public Task<ReportResult<SalesReportRow>> Sales(DateTime? from, DateTime? to, string? status, CancellationToken ct) => reports.SalesAsync(from, to, status, ct);

    [HttpGet("sales/csv")]
    public async Task<FileResult> SalesCsv(DateTime? from, DateTime? to, string? status, CancellationToken ct) =>
        CsvFile((await reports.SalesAsync(from, to, status, ct)).Rows, "ventas");

    [HttpGet("inventory")]
    public Task<ReportResult<InventoryReportRow>> Inventory(int? warehouseId, string? category, CancellationToken ct) =>
        reports.InventoryAsync(warehouseId, category, false, ct);

    [HttpGet("inventory/csv")]
    public async Task<FileResult> InventoryCsv(int? warehouseId, string? category, CancellationToken ct) =>
        CsvFile((await reports.InventoryAsync(warehouseId, category, false, ct)).Rows, "inventario");

    [HttpGet("low-stock")]
    public Task<ReportResult<InventoryReportRow>> LowStock(int? warehouseId, CancellationToken ct) => reports.InventoryAsync(warehouseId, null, true, ct);

    [HttpGet("low-stock/csv")]
    public async Task<FileResult> LowStockCsv(int? warehouseId, CancellationToken ct) =>
        CsvFile((await reports.InventoryAsync(warehouseId, null, true, ct)).Rows, "inventario-bajo");

    [HttpGet("production")]
    public Task<ReportResult<ProductionReportRow>> Production(DateTime? from, DateTime? to, string? status, CancellationToken ct) =>
        reports.ProductionAsync(from, to, status, ct);

    [HttpGet("production/csv")]
    public async Task<FileResult> ProductionCsv(DateTime? from, DateTime? to, string? status, CancellationToken ct) =>
        CsvFile((await reports.ProductionAsync(from, to, status, ct)).Rows, "produccion");

    /// <summary>
    /// Report-ready SQL Server views (the same objects Crystal Reports binds to). Only whitelisted view names
    /// are accepted, so the name never reaches SQL as user input. Available with the SQL Server provider only.
    /// </summary>
    public static readonly string[] ReportViews =
    [
        "vw_CurrentInventory", "vw_LowStockProducts", "vw_SalesByMonth", "vw_CustomerSalesSummary",
        "vw_ProductionStatus", "vw_InventoryMovementHistory", "vw_SalesReport"
    ];

    [HttpGet("views")]
    public object Views() => new { available = db.Database.IsSqlServer(), views = ReportViews };

    [HttpGet("views/{name}")]
    public async Task<IActionResult> View(string name, [FromQuery] int top = 500, CancellationToken ct = default)
    {
        var view = ReportViews.FirstOrDefault(v => v.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (view is null) return NotFound(new { detail = $"Unknown view '{name}'." });
        if (!db.Database.IsSqlServer())
            return Problem(statusCode: 501, title: "SQL Server only", detail: "Report views exist only when running on SQL Server.");

        var conn = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT TOP (@top) * FROM dbo.[{view}]";
            var p = cmd.CreateParameter();
            p.ParameterName = "@top";
            p.Value = Math.Clamp(top, 1, 5000);
            cmd.Parameters.Add(p);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return Ok(new { view, rows });
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private FileContentResult CsvFile<T>(IEnumerable<T> rows, string name) =>
        File(Csv.Write(rows), "text/csv; charset=utf-8", $"glasflow-{name}-{DateTime.UtcNow:yyyyMMdd}.csv");
}

[ApiController]
[Route("api/integration")]
public class IntegrationController(ImportService import, ProductService products, CustomerService customers) : ControllerBase
{
    /// <summary>Imports a product catalog CSV from an external ERP. Use dryRun=true to validate without saving.</summary>
    [HttpPost("products/import"), Authorize(Roles = Roles.Writers), RequestSizeLimit(5_000_000)]
    public async Task<ActionResult<ImportResult>> ImportProducts(IFormFile file, [FromQuery] bool dryRun = false, CancellationToken ct = default)
    {
        if (file.Length == 0) return BadRequest(new { detail = "The file is empty." });
        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return BadRequest(new { detail = "Only .csv files are accepted." });
        await using var stream = file.OpenReadStream();
        return await import.ImportProductsAsync(stream, dryRun, ct);
    }

    [HttpGet("products/export")]
    public async Task<FileResult> ExportProducts(CancellationToken ct)
    {
        var all = await products.ListAsync(new(1, 200), null, null, false, ct);
        var rows = all.Items.Select(p => new
        {
            ExternalId = p.ExternalId ?? "", p.ProductCode, p.ProductName, p.Category, p.Description, p.Unit, p.Cost, p.SellingPrice,
            p.MinimumStock, p.Active, StockAvailable = p.TotalAvailable, StockReserved = p.TotalReserved
        });
        return File(Csv.Write(rows), "text/csv; charset=utf-8", $"glasflow-productos-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    [HttpGet("customers/export")]
    public async Task<FileResult> ExportCustomers(CancellationToken ct)
    {
        var all = await customers.ListAsync(new(1, 200), null, ct);
        var rows = all.Items.Select(c => new
        {
            ExternalId = c.ExternalId ?? "", c.CustomerCode, c.Name, c.Rfc, c.Email, c.Phone, c.Address, c.City, c.State, c.PostalCode, c.TaxZone, c.CreditLimit, c.Active
        });
        return File(Csv.Write(rows), "text/csv; charset=utf-8", $"glasflow-clientes-{DateTime.UtcNow:yyyyMMdd}.csv");
    }

    /// <summary>The column and category mapping applied to ERP imports.</summary>
    [HttpGet("mapping")]
    public object Mapping() => new { columns = ImportService.ColumnMap, categories = ImportService.CategoryMap };

    /// <summary>A ready-to-edit sample CSV in the format of the fictional external ERP.</summary>
    [HttpGet("products/template"), AllowAnonymous]
    public FileResult Template()
    {
        const string csv = "erp_sku,code,name,familia,descripcion,uom,costo,precio,min_stock,proveedor\n" +
                           "SKU-90001,TMP-8MM,Vidrio templado 8 mm,TEMPERED,Templado para puertas de ducha,sqm,760,1210,40,PRV-001\n" +
                           "SKU-90002,DEC-GRB,Vidrio grabado decorativo 6 mm,DECORATIVE,Diseño floral,sqm,690,1100,15,PRV-001\n" +
                           "SKU-90003,ACC-JAL,Jaladera de acero inoxidable 30 cm,HARDWARE,Para puerta de vidrio,ea,240,420,30,PRV-002\n";
        return File(System.Text.Encoding.UTF8.GetBytes(csv), "text/csv", "erp-catalogo-ejemplo.csv");
    }
}

[ApiController]
[Route("api/assistant")]
public class AssistantController(AssistantService assistant) : ControllerBase
{
    [HttpGet("suggestions")]
    public IReadOnlyList<string> Suggestions() => AssistantService.SuggestedQuestions;

    /// <summary>Answers a business question from predefined, read-only data summaries (LLM optional; deterministic fallback).</summary>
    [HttpPost("ask"), EnableRateLimiting("assistant")]
    public Task<AssistantResponse> Ask(AssistantRequest request, CancellationToken ct) => assistant.AskAsync(request.Question, ct);
}

[ApiController]
[Route("api/system")]
public class SystemController(GlasFlowDbContext db, AssistantService assistant, IOptions<AiOptions> ai, IConfiguration config, IWebHostEnvironment env) : ControllerBase
{
    /// <summary>Application, database and AI provider status for the Settings page. Never returns secrets.</summary>
    [HttpGet("status")]
    public async Task<object> Status(CancellationToken ct)
    {
        var canConnect = await db.Database.CanConnectAsync(ct);
        var isSqlServer = db.Database.IsSqlServer();
        var objects = new List<object>();
        if (canConnect && isSqlServer)
            objects.AddRange(await db.Database.SqlQueryRaw<SqlObjectInfo>(
                "SELECT name AS Name, type_desc AS Type FROM sys.objects WHERE type IN ('P','V','TR') AND is_ms_shipped = 0 ORDER BY type_desc, name")
                .ToListAsync(ct));
        var active = assistant.ActiveProvider;
        return new
        {
            Application = new
            {
                Name = "GlasFlow ERP",
                Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3),
                Environment = env.EnvironmentName,
                Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                ServerTimeUtc = DateTime.UtcNow
            },
            Database = new
            {
                Provider = isSqlServer ? "SQL Server" : "SQLite",
                Connected = canConnect,
                Server = canConnect ? db.Database.GetDbConnection().DataSource : null,
                DatabaseName = canConnect ? db.Database.GetDbConnection().Database : null,
                Counts = canConnect ? new
                {
                    Customers = await db.Customers.CountAsync(ct),
                    Products = await db.Products.CountAsync(ct),
                    SalesOrders = await db.SalesOrders.CountAsync(ct),
                    ProductionOrders = await db.ProductionOrders.CountAsync(ct),
                    Movements = await db.InventoryMovements.CountAsync(ct),
                    AuditLogs = await db.AuditLogs.CountAsync(ct)
                } : null,
                SqlObjects = objects
            },
            Ai = new
            {
                ConfiguredProvider = ai.Value.Provider,
                Active = active?.Name ?? "local-rules (deterministic fallback)",
                HasApiKey = !string.IsNullOrWhiteSpace(ai.Value.ApiKey)
            },
            Automation = new
            {
                InventoryCheckMinutes = config.GetValue("Automation:InventoryCheckMinutes", 15),
                LastScheduledRun = LowStockMonitor.LastRun,
                AllowNegativeStock = config.GetValue("Inventory:AllowNegativeStock", false)
            }
        };
    }

    /// <summary>Recent audit trail (application and database-trigger entries).</summary>
    [HttpGet("audit"), Authorize(Roles = Roles.Admin)]
    public async Task<IReadOnlyList<AuditLog>> Audit([FromQuery] int take = 100, [FromQuery] string? source = null, CancellationToken ct = default)
    {
        var q = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(source)) q = q.Where(a => a.Source == source);
        return await q.OrderByDescending(a => a.Id).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct);
    }

    public record SqlObjectInfo(string Name, string Type);
}
