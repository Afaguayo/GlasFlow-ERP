using System.Globalization;
using GlasFlow.Application.Common;
using GlasFlow.Application.Dtos;
using GlasFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace GlasFlow.Application.Services;

/// <summary>
/// ERP product-catalog import. Reads a CSV exported by an external ERP, validates every row, maps the ERP's
/// identifiers and category names onto GlasFlow's model, then creates or updates products in one transaction.
/// Rows with errors are reported and skipped; a dry run validates without saving.
/// </summary>
public class ImportService(IGlasFlowDbContext db, AuditService audit, NotificationService notifications, TimeProvider clock)
{
    /// <summary>Header names accepted for each GlasFlow field (ERP column name -> field).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ColumnMap = new Dictionary<string, string[]>
    {
        ["ExternalId"] = ["externalid", "erp_sku", "sku", "item_id"],
        ["ProductCode"] = ["productcode", "code", "codigo"],
        ["ProductName"] = ["productname", "name", "descripcion_corta", "nombre"],
        ["Category"] = ["category", "categoria", "familia"],
        ["Description"] = ["description", "descripcion"],
        ["Unit"] = ["unit", "uom", "unidad"],
        ["Cost"] = ["cost", "costo"],
        ["SellingPrice"] = ["sellingprice", "price", "precio"],
        ["MinimumStock"] = ["minimumstock", "min_stock", "minimo"],
        ["SupplierCode"] = ["suppliercode", "vendor", "proveedor"]
    };

    /// <summary>External ERP family names mapped to GlasFlow categories.</summary>
    public static readonly IReadOnlyDictionary<string, string> CategoryMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CLEAR"] = ProductCategories.Clear, ["FLOAT"] = ProductCategories.Clear, [ProductCategories.Clear] = ProductCategories.Clear,
        ["TEMPERED"] = ProductCategories.Tempered, [ProductCategories.Tempered] = ProductCategories.Tempered,
        ["LAMINATED"] = ProductCategories.Laminated, [ProductCategories.Laminated] = ProductCategories.Laminated,
        ["DECORATIVE"] = ProductCategories.Decorative, ["FROSTED"] = ProductCategories.Decorative, [ProductCategories.Decorative] = ProductCategories.Decorative,
        ["ACCESSORY"] = ProductCategories.Accessories, ["HARDWARE"] = ProductCategories.Accessories, [ProductCategories.Accessories] = ProductCategories.Accessories
    };

    private static readonly Dictionary<string, string> UnitMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["m2"] = "m2", ["sqm"] = "m2", ["m²"] = "m2", ["pza"] = "pza", ["pc"] = "pza", ["ea"] = "pza", ["pieza"] = "pza",
        ["ml"] = "ml", ["m"] = "ml", ["kg"] = "kg"
    };

    public async Task<ImportResult> ImportProductsAsync(Stream csv, bool dryRun, CancellationToken ct)
    {
        using var reader = new StreamReader(csv);
        var rows = Csv.Read(reader);
        if (rows.Count == 0) throw new DomainException("The file is empty.");

        var header = rows[0].Select(h => h.Trim().TrimStart('﻿').ToLowerInvariant()).ToArray();
        var index = ColumnMap.ToDictionary(kv => kv.Key, kv => Array.FindIndex(header, h => kv.Value.Contains(h)));
        var missing = new[] { "ExternalId", "ProductName", "Category", "SellingPrice" }.Where(f => index[f] < 0).ToList();
        if (missing.Count > 0) throw new DomainException($"Missing required columns: {string.Join(", ", missing)}.");

        var suppliers = await db.Suppliers.ToDictionaryAsync(s => s.SupplierCode, s => s.Id, StringComparer.OrdinalIgnoreCase, ct);
        var products = await db.Products.ToListAsync(ct);
        var byExternal = products.Where(p => p.ExternalId != null).ToDictionary(p => p.ExternalId!, StringComparer.OrdinalIgnoreCase);
        var codes = products.ToDictionary(p => p.ProductCode, StringComparer.OrdinalIgnoreCase);
        var warehouses = await db.Warehouses.Where(w => w.Active).Select(w => w.Id).ToListAsync(ct);

        var errors = new List<ImportRowError>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int created = 0, updated = 0;
        var now = clock.GetUtcNow().UtcDateTime;
        var newProducts = new List<Product>();

        for (var r = 1; r < rows.Count; r++)
        {
            var line = r + 1;
            string Get(string field) => index[field] >= 0 && index[field] < rows[r].Length ? rows[r][index[field]].Trim() : "";
            var externalId = Get("ExternalId");
            try
            {
                if (externalId.Length == 0) throw new DomainException("ExternalId is required.");
                if (!seen.Add(externalId)) throw new DomainException("Duplicate ExternalId in file.");
                if (!CategoryMap.TryGetValue(Get("Category"), out var category)) throw new DomainException($"Unknown category '{Get("Category")}'.");
                var unitRaw = Get("Unit");
                if (!UnitMap.TryGetValue(unitRaw.Length == 0 ? "m2" : unitRaw, out var unit)) throw new DomainException($"Unknown unit '{unitRaw}'.");
                var price = ParseDecimal(Get("SellingPrice"), "SellingPrice");
                var cost = Get("Cost").Length == 0 ? 0 : ParseDecimal(Get("Cost"), "Cost");
                var min = Get("MinimumStock").Length == 0 ? 0 : ParseDecimal(Get("MinimumStock"), "MinimumStock");
                if (price < cost) throw new DomainException("SellingPrice is lower than Cost.");
                int? supplierId = null;
                var supplierCode = Get("SupplierCode");
                if (supplierCode.Length > 0)
                    supplierId = suppliers.TryGetValue(supplierCode, out var sid) ? sid : throw new DomainException($"Unknown supplier '{supplierCode}'.");

                var name = Get("ProductName");
                if (name.Length < 2) throw new DomainException("ProductName is required.");

                // All checks passed: only now is the catalog touched, so a failing row never leaves partial changes.
                if (!byExternal.TryGetValue(externalId, out var product))
                {
                    var code = Get("ProductCode").Length > 0 ? Get("ProductCode").ToUpperInvariant() : $"ERP-{externalId}".ToUpperInvariant();
                    if (codes.ContainsKey(code)) throw new DomainException($"ProductCode '{code}' already belongs to another product.");
                    product = new Product { ProductCode = code, ExternalId = externalId, CreatedAt = now, Active = true };
                    codes[code] = product;
                    byExternal[externalId] = product;
                    newProducts.Add(product);
                    created++;
                }
                else updated++;

                product.ProductName = name;
                product.Category = category;
                product.Description = Get("Description") is { Length: > 0 } d ? d : product.Description;
                product.Unit = unit;
                product.Cost = cost;
                product.SellingPrice = price;
                product.MinimumStock = min;
                product.SupplierId = supplierId ?? product.SupplierId;
                product.UpdatedAt = now;
            }
            catch (DomainException ex)
            {
                errors.Add(new ImportRowError(line, externalId.Length > 0 ? externalId : null, ex.Message));
            }
        }

        var result = new ImportResult(rows.Count - 1, created, updated, errors.Count, errors, dryRun);
        if (dryRun || (created + updated) == 0) return result;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Products.AddRange(newProducts);
        await db.SaveChangesAsync(ct);
        foreach (var p in newProducts)
            foreach (var w in warehouses) db.Inventory.Add(new InventoryItem { ProductId = p.Id, WarehouseId = w, UpdatedAt = now });
        audit.Record("ProductCatalogImported", nameof(Product), null, $"rows {result.TotalRows}, created {created}, updated {updated}, errors {errors.Count}");
        notifications.Add(NotificationType.ImportCompleted, errors.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
            "Importación de catálogo terminada", $"{created} productos creados, {updated} actualizados, {errors.Count} filas con error.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static decimal ParseDecimal(string s, string field) =>
        decimal.TryParse(s.Replace("$", "").Replace(" ", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v : throw new DomainException($"{field} '{s}' is not a valid non-negative number.");
}
