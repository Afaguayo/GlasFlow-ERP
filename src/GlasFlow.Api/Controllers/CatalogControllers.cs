using GlasFlow.Api.Infrastructure;
using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Application.Services;
using GlasFlow.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace GlasFlow.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(AuthService auth) : ControllerBase
{
    /// <summary>Exchanges username/password for a JWT bearer token.</summary>
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct) =>
        await auth.LoginAsync(request, ct) is { } response
            ? response
            : Problem(statusCode: 401, title: "Invalid credentials", detail: "Username or password is incorrect.");

    [HttpGet("me")]
    public object Me() => new
    {
        Username = User.Identity?.Name,
        DisplayName = User.FindFirst("display_name")?.Value,
        Role = User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
    };
}

[ApiController]
[Route("api/customers")]
public class CustomersController(CustomerService customers) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<CustomerDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null, [FromQuery] bool? active = null, CancellationToken ct = default) =>
        customers.ListAsync(new PageQuery(page, pageSize, search), active, ct);

    /// <summary>Customer with totals and recent order history.</summary>
    [HttpGet("{id:int}")]
    public Task<CustomerDetailDto> Get(int id, CancellationToken ct) => customers.GetAsync(id, ct);

    [HttpPost, Authorize(Roles = Roles.Writers)]
    public async Task<ActionResult<CustomerDto>> Create(CustomerRequest request, CancellationToken ct)
    {
        var created = await customers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}"), Authorize(Roles = Roles.Writers)]
    public Task<CustomerDto> Update(int id, CustomerRequest request, CancellationToken ct) => customers.UpdateAsync(id, request, ct);

    /// <summary>Deletes a customer without orders; customers with orders are deactivated.</summary>
    [HttpDelete("{id:int}"), Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await customers.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/suppliers")]
public class SuppliersController(SupplierService suppliers) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<SupplierDto>> List([FromQuery] string? search, CancellationToken ct) => suppliers.ListAsync(search, ct);

    [HttpGet("{id:int}")]
    public Task<SupplierDto> Get(int id, CancellationToken ct) => suppliers.GetAsync(id, ct);

    [HttpPost, Authorize(Roles = Roles.Writers)]
    public async Task<ActionResult<SupplierDto>> Create(SupplierRequest request, CancellationToken ct)
    {
        var created = await suppliers.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}"), Authorize(Roles = Roles.Writers)]
    public Task<SupplierDto> Update(int id, SupplierRequest request, CancellationToken ct) => suppliers.UpdateAsync(id, request, ct);

    [HttpDelete("{id:int}"), Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await suppliers.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/products")]
public class ProductsController(ProductService products) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<ProductDto>> List([FromQuery] int page = 1, [FromQuery] int pageSize = 25, [FromQuery] string? search = null,
        [FromQuery] string? category = null, [FromQuery] bool? active = null, [FromQuery] bool lowStockOnly = false, CancellationToken ct = default) =>
        products.ListAsync(new PageQuery(page, pageSize, search), category, active, lowStockOnly, ct);

    [HttpGet("categories")]
    public IReadOnlyList<string> Categories() => ProductCategories.All;

    [HttpGet("{id:int}")]
    public Task<ProductDto> Get(int id, CancellationToken ct) => products.GetAsync(id, ct);

    [HttpPost, Authorize(Roles = Roles.Writers)]
    public async Task<ActionResult<ProductDto>> Create(ProductRequest request, CancellationToken ct)
    {
        var created = await products.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}"), Authorize(Roles = Roles.Writers)]
    public Task<ProductDto> Update(int id, ProductRequest request, CancellationToken ct) => products.UpdateAsync(id, request, ct);

    /// <summary>Soft delete (deactivation): products are referenced by orders and movements.</summary>
    [HttpDelete("{id:int}"), Authorize(Roles = Roles.Admin)]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        await products.DeactivateAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/warehouses")]
public class WarehousesController(WarehouseService warehouses) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<WarehouseDto>> List(CancellationToken ct) => warehouses.ListAsync(ct);

    [HttpPost, Authorize(Roles = Roles.Admin)]
    public Task<WarehouseDto> Create(WarehouseRequest request, CancellationToken ct) => warehouses.CreateAsync(request, ct);
}
