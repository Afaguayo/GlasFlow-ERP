using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Application.Services;
using GlasFlow.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GlasFlow.Api.Controllers;

[ApiController]
[Route("api/inventory")]
public class InventoryController(InventoryService inventory, InventoryAlertService alerts) : ControllerBase
{
    /// <summary>Stock per product and warehouse, filterable by search, category, warehouse and low stock.</summary>
    [HttpGet]
    public Task<PagedResult<StockDto>> Stock([FromQuery] StockQuery query, CancellationToken ct) => inventory.ListStockAsync(query, ct);

    [HttpGet("movements")]
    public Task<PagedResult<MovementDto>> Movements([FromQuery] MovementQuery query, CancellationToken ct) => inventory.ListMovementsAsync(query, ct);

    /// <summary>Signed stock adjustment (cycle count, breakage...). Recorded as an Adjustment movement.</summary>
    [HttpPost("adjustments"), Authorize(Roles = Roles.Writers)]
    public Task<StockDto> Adjust(StockAdjustmentRequest request, CancellationToken ct) => inventory.AdjustAsync(request, ct);

    [HttpPost("transfers"), Authorize(Roles = Roles.Writers)]
    public async Task<IActionResult> Transfer(StockTransferRequest request, CancellationToken ct)
    {
        await inventory.TransferAsync(request, ct);
        return NoContent();
    }

    /// <summary>Manually triggers the low-stock automation (also runs on a schedule). Idempotent.</summary>
    [HttpPost("check"), Authorize(Roles = Roles.Writers)]
    public Task<InventoryCheckResult> RunCheck(CancellationToken ct) => alerts.RunFullCheckAsync(ct);
}

[ApiController]
[Route("api/sales-orders")]
public class SalesOrdersController(SalesOrderService orders) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<SalesOrderSummaryDto>> List([FromQuery] SalesOrderQuery query, CancellationToken ct) => orders.ListAsync(query, ct);

    [HttpGet("{id:int}")]
    public Task<SalesOrderDto> Get(int id, CancellationToken ct) => orders.GetAsync(id, ct);

    /// <summary>Creates a Draft order. Totals and IVA (16% general, 8% border zone) are calculated server-side.</summary>
    [HttpPost, Authorize(Roles = Roles.Writers)]
    public async Task<ActionResult<SalesOrderDto>> Create(CreateSalesOrderRequest request, CancellationToken ct)
    {
        var created = await orders.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>Draft -> Confirmed. Reserves stock for every line or fails without changes.</summary>
    [HttpPost("{id:int}/confirm"), Authorize(Roles = Roles.Writers)]
    public Task<SalesOrderDto> Confirm(int id, CancellationToken ct) => orders.ConfirmAsync(id, ct);

    /// <summary>Confirmed -> Shipped. Deducts the reserved stock.</summary>
    [HttpPost("{id:int}/ship"), Authorize(Roles = Roles.Writers)]
    public Task<SalesOrderDto> Ship(int id, CancellationToken ct) => orders.ShipAsync(id, ct);

    /// <summary>Draft/Confirmed -> Cancelled. Releases any reservation.</summary>
    [HttpPost("{id:int}/cancel"), Authorize(Roles = Roles.Writers)]
    public Task<SalesOrderDto> Cancel(int id, CancellationToken ct) => orders.CancelAsync(id, ct);
}

[ApiController]
[Route("api/production-orders")]
public class ProductionOrdersController(ProductionService production) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<ProductionOrderDto>> List([FromQuery] ProductionQuery query, CancellationToken ct) => production.ListAsync(query, ct);

    [HttpGet("{id:int}")]
    public Task<ProductionOrderDto> Get(int id, CancellationToken ct) => production.GetAsync(id, ct);

    [HttpPost, Authorize(Roles = Roles.Writers)]
    public async Task<ActionResult<ProductionOrderDto>> Create(CreateProductionOrderRequest request, CancellationToken ct)
    {
        var created = await production.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPost("{id:int}/start"), Authorize(Roles = Roles.Writers)]
    public Task<ProductionOrderDto> Start(int id, CancellationToken ct) => production.StartAsync(id, ct);

    /// <summary>Registers finished units; they are received into the order's warehouse immediately.</summary>
    [HttpPost("{id:int}/register"), Authorize(Roles = Roles.Writers)]
    public Task<ProductionOrderDto> Register(int id, RegisterProductionRequest request, CancellationToken ct) =>
        production.RegisterAsync(id, request.Quantity, ct);

    /// <summary>Completes the order, optionally registering a final quantity in the same transaction.</summary>
    [HttpPost("{id:int}/complete"), Authorize(Roles = Roles.Writers)]
    public Task<ProductionOrderDto> Complete(int id, [FromBody] CompleteProductionRequest? request, CancellationToken ct) =>
        production.CompleteAsync(id, request?.Quantity, ct);

    [HttpPost("{id:int}/cancel"), Authorize(Roles = Roles.Writers)]
    public Task<ProductionOrderDto> Cancel(int id, CancellationToken ct) => production.CancelAsync(id, ct);
}

[ApiController]
[Route("api/notifications")]
public class NotificationsController(NotificationService notifications) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<NotificationDto>> List([FromQuery] bool unreadOnly = false, [FromQuery] bool includeResolved = false,
        [FromQuery] int take = 50, CancellationToken ct = default) => notifications.ListAsync(unreadOnly, includeResolved, take, ct);

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct)
    {
        await notifications.MarkReadAsync(id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<object> MarkAllRead(CancellationToken ct) => new { updated = await notifications.MarkAllReadAsync(ct) };
}

[ApiController]
[Route("api/dashboard")]
public class DashboardController(DashboardService dashboard) : ControllerBase
{
    [HttpGet]
    public Task<DashboardDto> Get(CancellationToken ct) => dashboard.GetAsync(ct);
}
