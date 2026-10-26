using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GlasFlow.Application.Services;

public class CustomerService(IGlasFlowDbContext db, AuditService audit, TimeProvider clock)
{
    public Task<PagedResult<CustomerDto>> ListAsync(PageQuery q, bool? active, CancellationToken ct)
    {
        var query = db.Customers.AsNoTracking();
        if (active is not null) query = query.Where(c => c.Active == active);
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(c => c.Name.Contains(s) || c.CustomerCode.Contains(s) || (c.Rfc != null && c.Rfc.Contains(s)) || c.City.Contains(s));
        }
        return query.OrderBy(c => c.Name).ToPagedAsync(q, c => c.ToDto(), ct);
    }

    public async Task<CustomerDetailDto> GetAsync(int id, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new NotFoundException("Customer", id);
        var orders = await db.SalesOrders.AsNoTracking().Include(o => o.Customer).Include(o => o.Warehouse).Include(o => o.Details)
            .Where(o => o.CustomerId == id).OrderByDescending(o => o.OrderDate).ToListAsync(ct);
        var billed = orders.Where(o => o.Status is SalesOrderStatus.Confirmed or SalesOrderStatus.Shipped).Sum(o => o.Total);
        return new CustomerDetailDto(customer.ToDto(), billed, orders.Count, orders.Take(20).Select(o => o.ToSummary()).ToList());
    }

    public async Task<CustomerDto> CreateAsync(CustomerRequest r, CancellationToken ct)
    {
        await EnsureUniqueAsync(r, null, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var c = new Customer { CreatedAt = now };
        Apply(c, r, now);
        db.Customers.Add(c);
        await db.SaveChangesAsync(ct);
        audit.Record("CustomerCreated", nameof(Customer), c.Id, c.CustomerCode);
        await db.SaveChangesAsync(ct);
        return c.ToDto();
    }

    public async Task<CustomerDto> UpdateAsync(int id, CustomerRequest r, CancellationToken ct)
    {
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer", id);
        await EnsureUniqueAsync(r, id, ct);
        Apply(c, r, clock.GetUtcNow().UtcDateTime);
        audit.Record("CustomerUpdated", nameof(Customer), c.Id, c.CustomerCode);
        await db.SaveChangesAsync(ct);
        return c.ToDto();
    }

    /// <summary>Customers with orders are deactivated instead of deleted, to keep order history intact.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var c = await db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Customer", id);
        if (await db.SalesOrders.AnyAsync(o => o.CustomerId == id, ct))
        {
            c.Active = false;
            c.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            audit.Record("CustomerDeactivated", nameof(Customer), id, "Has orders; deactivated instead of deleted");
        }
        else
        {
            db.Customers.Remove(c);
            audit.Record("CustomerDeleted", nameof(Customer), id, c.CustomerCode);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureUniqueAsync(CustomerRequest r, int? id, CancellationToken ct)
    {
        if (await db.Customers.AnyAsync(c => c.CustomerCode == r.CustomerCode && c.Id != id, ct))
            throw new ConflictException($"Customer code '{r.CustomerCode}' already exists.");
        if (r.ExternalId is not null && await db.Customers.AnyAsync(c => c.ExternalId == r.ExternalId && c.Id != id, ct))
            throw new ConflictException($"External id '{r.ExternalId}' is already mapped to another customer.");
    }

    private static void Apply(Customer c, CustomerRequest r, DateTime now)
    {
        c.CustomerCode = r.CustomerCode.Trim().ToUpperInvariant();
        c.ExternalId = string.IsNullOrWhiteSpace(r.ExternalId) ? null : r.ExternalId.Trim();
        c.Name = r.Name.Trim();
        c.Rfc = string.IsNullOrWhiteSpace(r.Rfc) ? null : r.Rfc.Trim().ToUpperInvariant();
        c.ContactName = r.ContactName;
        c.Email = r.Email;
        c.Phone = r.Phone;
        c.Address = r.Address;
        c.City = r.City.Trim();
        c.State = r.State.Trim();
        c.PostalCode = r.PostalCode;
        c.TaxZone = Enum.Parse<TaxZone>(r.TaxZone);
        c.CreditLimit = r.CreditLimit;
        c.Active = r.Active;
        c.UpdatedAt = now;
    }
}

public class SupplierService(IGlasFlowDbContext db, AuditService audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<SupplierDto>> ListAsync(string? search, CancellationToken ct)
    {
        var query = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search)) query = query.Where(s => s.Name.Contains(search) || s.SupplierCode.Contains(search));
        return await query.OrderBy(s => s.Name)
            .Select(s => new SupplierDto(s.Id, s.SupplierCode, s.Name, s.Rfc, s.ContactName, s.Email, s.Phone, s.City, s.State, s.Active, s.Products.Count))
            .ToListAsync(ct);
    }

    public async Task<SupplierDto> GetAsync(int id, CancellationToken ct) =>
        (await ListAsync(null, ct)).FirstOrDefault(s => s.Id == id) ?? throw new NotFoundException("Supplier", id);

    public async Task<SupplierDto> CreateAsync(SupplierRequest r, CancellationToken ct)
    {
        if (await db.Suppliers.AnyAsync(s => s.SupplierCode == r.SupplierCode, ct))
            throw new ConflictException($"Supplier code '{r.SupplierCode}' already exists.");
        var now = clock.GetUtcNow().UtcDateTime;
        var s = new Supplier { CreatedAt = now };
        Apply(s, r, now);
        db.Suppliers.Add(s);
        await db.SaveChangesAsync(ct);
        audit.Record("SupplierCreated", nameof(Supplier), s.Id, s.SupplierCode);
        await db.SaveChangesAsync(ct);
        return await GetAsync(s.Id, ct);
    }

    public async Task<SupplierDto> UpdateAsync(int id, SupplierRequest r, CancellationToken ct)
    {
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier", id);
        if (await db.Suppliers.AnyAsync(x => x.SupplierCode == r.SupplierCode && x.Id != id, ct))
            throw new ConflictException($"Supplier code '{r.SupplierCode}' already exists.");
        Apply(s, r, clock.GetUtcNow().UtcDateTime);
        audit.Record("SupplierUpdated", nameof(Supplier), id, s.SupplierCode);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var s = await db.Suppliers.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Supplier", id);
        if (await db.Products.AnyAsync(p => p.SupplierId == id, ct))
            throw new ConflictException("Supplier has products assigned; deactivate it instead.");
        db.Suppliers.Remove(s);
        audit.Record("SupplierDeleted", nameof(Supplier), id, s.SupplierCode);
        await db.SaveChangesAsync(ct);
    }

    private static void Apply(Supplier s, SupplierRequest r, DateTime now)
    {
        s.SupplierCode = r.SupplierCode.Trim().ToUpperInvariant();
        s.Name = r.Name.Trim();
        s.Rfc = string.IsNullOrWhiteSpace(r.Rfc) ? null : r.Rfc.Trim().ToUpperInvariant();
        s.ContactName = r.ContactName;
        s.Email = r.Email;
        s.Phone = r.Phone;
        s.City = r.City;
        s.State = r.State;
        s.Active = r.Active;
        s.UpdatedAt = now;
    }
}

public class ProductService(IGlasFlowDbContext db, AuditService audit, TimeProvider clock)
{
    public async Task<PagedResult<ProductDto>> ListAsync(PageQuery q, string? category, bool? active, bool lowStockOnly, CancellationToken ct)
    {
        // Filter and sort on the entity (translatable to SQL), then project only the requested page.
        var query = db.Products.AsNoTracking();
        if (active is not null) query = query.Where(p => p.Active == active);
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(p => p.Category == category);
        if (lowStockOnly)
            query = query.Where(p => p.MinimumStock > 0 && p.Inventory.Any(i => i.QuantityAvailable - i.QuantityReserved <= p.MinimumStock));
        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var s = q.Search.Trim();
            query = query.Where(p => p.ProductName.Contains(s) || p.ProductCode.Contains(s) || (p.ExternalId != null && p.ExternalId.Contains(s)));
        }
        var total = await query.CountAsync(ct);
        var items = await Project(query.OrderBy(p => p.ProductCode).Skip((q.SafePage - 1) * q.SafePageSize).Take(q.SafePageSize)).ToListAsync(ct);
        return new PagedResult<ProductDto>(items, q.SafePage, q.SafePageSize, total);
    }

    public async Task<ProductDto> GetAsync(int id, CancellationToken ct) =>
        await Project(db.Products.AsNoTracking().Where(p => p.Id == id)).FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException("Product", id);

    public async Task<ProductDto> CreateAsync(ProductRequest r, CancellationToken ct)
    {
        await EnsureValidAsync(r, null, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var p = new Product { CreatedAt = now };
        Apply(p, r, now);
        p.Validate();
        db.Products.Add(p);
        await db.SaveChangesAsync(ct);

        // Every product gets a zero stock row in each active warehouse so it appears in stock screens.
        var warehouses = await db.Warehouses.Where(w => w.Active).Select(w => w.Id).ToListAsync(ct);
        foreach (var w in warehouses) db.Inventory.Add(new InventoryItem { ProductId = p.Id, WarehouseId = w, UpdatedAt = now });
        audit.Record("ProductCreated", nameof(Product), p.Id, p.ProductCode);
        await db.SaveChangesAsync(ct);
        return await GetAsync(p.Id, ct);
    }

    public async Task<ProductDto> UpdateAsync(int id, ProductRequest r, CancellationToken ct)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product", id);
        await EnsureValidAsync(r, id, ct);
        var oldPrice = p.SellingPrice;
        Apply(p, r, clock.GetUtcNow().UtcDateTime);
        p.Validate();
        audit.Record("ProductUpdated", nameof(Product), id,
            oldPrice != p.SellingPrice ? $"{p.ProductCode} price {oldPrice:0.00} -> {p.SellingPrice:0.00}" : p.ProductCode);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Soft delete: products are referenced by orders and movements, so they are deactivated.</summary>
    public async Task DeactivateAsync(int id, CancellationToken ct)
    {
        var p = await db.Products.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Product", id);
        p.Active = false;
        p.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        audit.Record("ProductDeactivated", nameof(Product), id, p.ProductCode);
        await db.SaveChangesAsync(ct);
    }

    private async Task EnsureValidAsync(ProductRequest r, int? id, CancellationToken ct)
    {
        if (!ProductCategories.All.Contains(r.Category))
            throw new DomainException($"Unknown category '{r.Category}'. Valid: {string.Join(", ", ProductCategories.All)}.");
        if (r.SellingPrice < r.Cost)
            throw new DomainException("Selling price cannot be lower than cost.");
        var code = r.ProductCode.Trim().ToUpperInvariant();
        if (await db.Products.AnyAsync(p => p.ProductCode == code && p.Id != id, ct))
            throw new ConflictException($"Product code '{code}' already exists.");
        if (r.ExternalId is not null && await db.Products.AnyAsync(p => p.ExternalId == r.ExternalId && p.Id != id, ct))
            throw new ConflictException($"External id '{r.ExternalId}' is already mapped to another product.");
        if (r.SupplierId is not null && !await db.Suppliers.AnyAsync(s => s.Id == r.SupplierId, ct))
            throw new NotFoundException("Supplier", r.SupplierId);
    }

    private static void Apply(Product p, ProductRequest r, DateTime now)
    {
        p.ProductCode = r.ProductCode.Trim().ToUpperInvariant();
        p.ExternalId = string.IsNullOrWhiteSpace(r.ExternalId) ? null : r.ExternalId.Trim();
        p.ProductName = r.ProductName.Trim();
        p.Category = r.Category;
        p.Description = r.Description;
        p.Unit = r.Unit;
        p.Cost = r.Cost;
        p.SellingPrice = r.SellingPrice;
        p.MinimumStock = r.MinimumStock;
        p.Active = r.Active;
        p.SupplierId = r.SupplierId;
        p.UpdatedAt = now;
    }

    internal static IQueryable<ProductDto> Project(IQueryable<Product> source) => source.Select(p => new ProductDto(
        p.Id, p.ProductCode, p.ExternalId, p.ProductName, p.Category, p.Description, p.Unit, p.Cost, p.SellingPrice,
        p.MinimumStock, p.Active, p.SupplierId, p.Supplier != null ? p.Supplier.Name : null,
        p.Inventory.Sum(i => i.QuantityAvailable), p.Inventory.Sum(i => i.QuantityReserved),
        p.MinimumStock > 0 && p.Inventory.Any(i => i.QuantityAvailable - i.QuantityReserved <= p.MinimumStock)));
}

public class WarehouseService(IGlasFlowDbContext db, AuditService audit, TimeProvider clock)
{
    public async Task<IReadOnlyList<WarehouseDto>> ListAsync(CancellationToken ct) =>
        await db.Warehouses.AsNoTracking().OrderBy(w => w.Code)
            .Select(w => new WarehouseDto(w.Id, w.Code, w.Name, w.City, w.State, w.Active,
                db.Inventory.Count(i => i.WarehouseId == w.Id),
                db.Inventory.Where(i => i.WarehouseId == w.Id).Sum(i => i.QuantityAvailable * i.Product!.Cost)))
            .ToListAsync(ct);

    public async Task<WarehouseDto> CreateAsync(WarehouseRequest r, CancellationToken ct)
    {
        var code = r.Code.Trim().ToUpperInvariant();
        if (await db.Warehouses.AnyAsync(w => w.Code == code, ct)) throw new ConflictException($"Warehouse '{code}' already exists.");
        var now = clock.GetUtcNow().UtcDateTime;
        var w = new Warehouse { Code = code, Name = r.Name, City = r.City, State = r.State, Active = r.Active, CreatedAt = now, UpdatedAt = now };
        db.Warehouses.Add(w);
        await db.SaveChangesAsync(ct);
        var products = await db.Products.Select(p => p.Id).ToListAsync(ct);
        foreach (var p in products) db.Inventory.Add(new InventoryItem { ProductId = p, WarehouseId = w.Id, UpdatedAt = now });
        audit.Record("WarehouseCreated", nameof(Warehouse), w.Id, code);
        await db.SaveChangesAsync(ct);
        return (await ListAsync(ct)).First(x => x.Id == w.Id);
    }
}
