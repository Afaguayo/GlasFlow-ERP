/* =====================================================================================
   GlasFlow ERP — 04_views.sql
   Report-ready views. They are the datasets Crystal Reports (or any report designer / BI tool)
   binds to, and the API exposes them read-only at GET /api/reports/views/{name}
   (whitelist: ReportsController.ReportViews in src/GlasFlow.Api/Controllers/ReportingControllers.cs).

   Execution order:  01_schema.sql -> 02_seed_data.sql -> 03_stored_procedures.sql
                     -> 04_views.sql -> 05_triggers.sql -> 06_reports.sql (ad-hoc queries)

   No USE statement: run against the target database with sqlcmd -d GlasFlowERP.
   The API applies this file on every start-up (CREATE OR ALTER = idempotent).

   Shared definitions (identical to the C# domain):
   - Free stock      = QuantityAvailable - QuantityReserved
   - Low stock       = MinimumStock > 0 AND free stock <= MinimumStock   (MinimumStock 0 = not tracked)
   - Billed sales    = orders in status Confirmed or Shipped (Draft and Cancelled are excluded)
   - Stock value     = ROUND(QuantityAvailable * Cost, 2)
   Column aliases are explicit and stable so report designers can rely on them.
   Views contain no ORDER BY; sort in the report or in the query that reads them.
   ===================================================================================== */

/* Current stock of every active product in every warehouse, with valuation and status. */
CREATE OR ALTER VIEW dbo.vw_CurrentInventory
AS
SELECT
    i.Id                                                    AS InventoryId,
    p.Id                                                    AS ProductId,
    p.ProductCode,
    p.ProductName,
    p.Category,
    p.Unit,
    w.Id                                                    AS WarehouseId,
    w.Code                                                  AS WarehouseCode,
    w.Name                                                  AS WarehouseName,
    w.City                                                  AS WarehouseCity,
    i.QuantityAvailable,
    i.QuantityReserved,
    i.QuantityAvailable - i.QuantityReserved                AS QuantityFree,
    p.MinimumStock,
    p.Cost                                                  AS UnitCost,
    p.SellingPrice                                          AS UnitPrice,
    CAST(ROUND(i.QuantityAvailable * p.Cost, 2) AS decimal(18,2))         AS StockValue,
    CAST(ROUND(i.QuantityAvailable * p.SellingPrice, 2) AS decimal(18,2)) AS StockValueAtPrice,
    CAST(CASE WHEN p.MinimumStock > 0 AND i.QuantityAvailable - i.QuantityReserved <= p.MinimumStock
              THEN 1 ELSE 0 END AS bit)                     AS IsLowStock,
    CASE WHEN i.QuantityAvailable - i.QuantityReserved <= 0 THEN N'Sin existencia'
         WHEN p.MinimumStock > 0 AND i.QuantityAvailable - i.QuantityReserved <= p.MinimumStock THEN N'Bajo mínimo'
         ELSE N'OK' END                                     AS StockStatus,
    s.SupplierCode,
    s.Name                                                  AS SupplierName,
    i.Version                                               AS RowVersion,
    i.UpdatedAt                                             AS LastUpdatedUtc
FROM dbo.Inventory i
JOIN dbo.Products p        ON p.Id = i.ProductId
JOIN dbo.Warehouses w      ON w.Id = i.WarehouseId
LEFT JOIN dbo.Suppliers s  ON s.Id = p.SupplierId
WHERE p.Active = 1;
GO

/* Stock rows at or below their minimum, with the shortfall, a suggested reorder quantity
   (back up to twice the minimum) and the open LowStock alert, if any. */
CREATE OR ALTER VIEW dbo.vw_LowStockProducts
AS
SELECT
    p.Id                                                    AS ProductId,
    p.ProductCode,
    p.ProductName,
    p.Category,
    p.Unit,
    w.Id                                                    AS WarehouseId,
    w.Code                                                  AS WarehouseCode,
    w.Name                                                  AS WarehouseName,
    i.QuantityAvailable,
    i.QuantityReserved,
    i.QuantityAvailable - i.QuantityReserved                AS QuantityFree,
    p.MinimumStock,
    p.MinimumStock - (i.QuantityAvailable - i.QuantityReserved)       AS Shortfall,
    p.MinimumStock * 2 - (i.QuantityAvailable - i.QuantityReserved)   AS SuggestedReorderQty,
    CASE WHEN i.QuantityAvailable - i.QuantityReserved <= 0 THEN N'Critical' ELSE N'Warning' END AS Severity,
    CASE WHEN p.Category = N'Accesorios' THEN N'Compra' ELSE N'Producción' END AS ReplenishBy,
    s.SupplierCode,
    s.Name                                                  AS SupplierName,
    n.Id                                                    AS OpenAlertId,
    n.CreatedAt                                             AS AlertCreatedUtc
FROM dbo.Inventory i
JOIN dbo.Products p        ON p.Id = i.ProductId
JOIN dbo.Warehouses w      ON w.Id = i.WarehouseId
LEFT JOIN dbo.Suppliers s  ON s.Id = p.SupplierId
LEFT JOIN dbo.Notifications n
       ON n.DedupKey = CONCAT(N'LOWSTOCK:', i.ProductId, N':', i.WarehouseId) AND n.IsResolved = 0
WHERE p.Active = 1
  AND p.MinimumStock > 0
  AND i.QuantityAvailable - i.QuantityReserved <= p.MinimumStock;
GO

/* Billed sales (Confirmed + Shipped) per calendar month of OrderDate (UTC). */
CREATE OR ALTER VIEW dbo.vw_SalesByMonth
AS
SELECT
    YEAR(o.OrderDate)                                       AS SalesYear,
    MONTH(o.OrderDate)                                      AS SalesMonth,
    DATEFROMPARTS(YEAR(o.OrderDate), MONTH(o.OrderDate), 1) AS MonthStart,
    CONVERT(char(7), DATEFROMPARTS(YEAR(o.OrderDate), MONTH(o.OrderDate), 1), 126) AS YearMonth,
    COUNT(*)                                                AS OrderCount,
    COUNT(DISTINCT o.CustomerId)                            AS CustomerCount,
    SUM(CASE WHEN o.Status = N'Shipped' THEN 1 ELSE 0 END)  AS ShippedOrders,
    SUM(o.Subtotal)                                         AS Subtotal,
    SUM(o.Tax)                                              AS Tax,
    SUM(o.Total)                                            AS Total,
    CAST(ROUND(AVG(o.Total), 2) AS decimal(18,2))           AS AverageOrderTotal
FROM dbo.SalesOrders o
WHERE o.Status IN (N'Confirmed', N'Shipped')
GROUP BY YEAR(o.OrderDate), MONTH(o.OrderDate);
GO

/* One row per customer (also customers without orders) with billed totals and credit exposure.
   OpenOrdersTotal = confirmed but not yet shipped; CreditAvailable = CreditLimit - OpenOrdersTotal. */
CREATE OR ALTER VIEW dbo.vw_CustomerSalesSummary
AS
SELECT
    c.Id                                                    AS CustomerId,
    c.CustomerCode,
    c.Name                                                  AS CustomerName,
    c.Rfc,
    c.City,
    c.State,
    c.TaxZone,
    c.CreditLimit,
    c.Active                                                AS IsActive,
    ISNULL(a.OrderCount, 0)                                 AS OrderCount,
    ISNULL(a.Subtotal, 0)                                   AS BilledSubtotal,
    ISNULL(a.Tax, 0)                                        AS BilledTax,
    ISNULL(a.Total, 0)                                      AS BilledTotal,
    CAST(ISNULL(ROUND(a.Total / NULLIF(a.OrderCount, 0), 2), 0) AS decimal(18,2)) AS AverageOrderTotal,
    a.FirstOrderDate,
    a.LastOrderDate,
    ISNULL(a.OpenOrdersTotal, 0)                            AS OpenOrdersTotal,
    c.CreditLimit - ISNULL(a.OpenOrdersTotal, 0)            AS CreditAvailable
FROM dbo.Customers c
LEFT JOIN (
    SELECT o.CustomerId,
           COUNT(*)            AS OrderCount,
           SUM(o.Subtotal)     AS Subtotal,
           SUM(o.Tax)          AS Tax,
           SUM(o.Total)        AS Total,
           MIN(o.OrderDate)    AS FirstOrderDate,
           MAX(o.OrderDate)    AS LastOrderDate,
           SUM(CASE WHEN o.Status = N'Confirmed' THEN o.Total ELSE 0 END) AS OpenOrdersTotal
    FROM dbo.SalesOrders o
    WHERE o.Status IN (N'Confirmed', N'Shipped')
    GROUP BY o.CustomerId
) a ON a.CustomerId = c.Id;
GO

/* Production orders with progress, pending quantity and due-date status. */
CREATE OR ALTER VIEW dbo.vw_ProductionStatus
AS
SELECT
    po.Id                                                   AS ProductionOrderId,
    po.ProductionNumber,
    p.ProductCode,
    p.ProductName,
    p.Category,
    p.Unit,
    w.Code                                                  AS WarehouseCode,
    w.Name                                                  AS WarehouseName,
    po.Status,
    CASE po.Status WHEN N'Planned' THEN N'Planeada' WHEN N'InProgress' THEN N'En proceso'
                   WHEN N'Completed' THEN N'Terminada' WHEN N'Cancelled' THEN N'Cancelada' END AS StatusLabel,
    po.QuantityPlanned,
    po.QuantityCompleted,
    po.QuantityPlanned - po.QuantityCompleted               AS QuantityPending,
    CAST(ROUND(po.QuantityCompleted * 100.0 / po.QuantityPlanned, 1) AS decimal(5,1)) AS ProgressPercent,
    po.CreatedAt                                            AS CreatedUtc,
    po.StartDate,
    po.DueDate,
    po.CompletionDate,
    CAST(CASE WHEN po.Status IN (N'Planned', N'InProgress') AND po.DueDate < CAST(SYSUTCDATETIME() AS date)
              THEN 1 ELSE 0 END AS bit)                     AS IsOverdue,
    CAST(CASE WHEN po.Status = N'Completed' AND po.DueDate IS NOT NULL
              THEN CASE WHEN CAST(po.CompletionDate AS date) <= CAST(po.DueDate AS date) THEN 1 ELSE 0 END
         END AS bit)                                        AS CompletedOnTime,
    CASE WHEN po.Status = N'Completed' AND po.DueDate IS NOT NULL
         THEN DATEDIFF(day, po.DueDate, po.CompletionDate) END AS DaysLateAtCompletion,
    CASE WHEN po.StartDate IS NOT NULL
         THEN DATEDIFF(day, po.StartDate, ISNULL(po.CompletionDate, SYSUTCDATETIME())) END AS DaysInProduction,
    po.Notes
FROM dbo.ProductionOrders po
JOIN dbo.Products p   ON p.Id = po.ProductId
JOIN dbo.Warehouses w ON w.Id = po.WarehouseId;
GO

/* Inventory movement ledger with product/warehouse names, a direction label and the running
   on-hand balance per product x warehouse. Reservation/ReservationRelease movements do not change
   on-hand stock (AffectsOnHand = 0), so they are excluded from the running balance. */
CREATE OR ALTER VIEW dbo.vw_InventoryMovementHistory
AS
SELECT
    m.Id                                                    AS MovementId,
    m.[Timestamp]                                           AS MovementUtc,
    CAST(m.[Timestamp] AS date)                             AS MovementDate,
    p.Id                                                    AS ProductId,
    p.ProductCode,
    p.ProductName,
    p.Category,
    p.Unit,
    w.Id                                                    AS WarehouseId,
    w.Code                                                  AS WarehouseCode,
    m.MovementType,
    CASE m.MovementType
        WHEN N'Adjustment'         THEN N'Ajuste'
        WHEN N'Reservation'        THEN N'Reserva'
        WHEN N'ReservationRelease' THEN N'Liberación de reserva'
        WHEN N'SaleShipment'       THEN N'Embarque de venta'
        WHEN N'ProductionReceipt'  THEN N'Entrada de producción'
        WHEN N'TransferOut'        THEN N'Traspaso salida'
        WHEN N'TransferIn'         THEN N'Traspaso entrada'
        WHEN N'Import'             THEN N'Importación / saldo inicial'
    END                                                     AS MovementTypeLabel,
    CASE WHEN m.MovementType IN (N'Reservation', N'ReservationRelease') THEN N'Reserva'
         WHEN m.Quantity >= 0 THEN N'Entrada' ELSE N'Salida' END AS Direction,
    CAST(CASE WHEN m.MovementType IN (N'Reservation', N'ReservationRelease') THEN 0 ELSE 1 END AS bit) AS AffectsOnHand,
    m.Quantity,
    SUM(CASE WHEN m.MovementType IN (N'Reservation', N'ReservationRelease') THEN 0 ELSE m.Quantity END)
        OVER (PARTITION BY m.ProductId, m.WarehouseId ORDER BY m.[Timestamp], m.Id
              ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningOnHand,
    m.Reference,
    m.Notes,
    m.UserName
FROM dbo.InventoryMovements m
JOIN dbo.Products p   ON p.Id = m.ProductId
JOIN dbo.Warehouses w ON w.Id = m.WarehouseId;
GO

/* Crystal Reports sales dataset: one row per order line with header, customer and product columns.
   Header amounts (OrderSubtotal/OrderTax/OrderTotal) repeat on every line of the order: sum LineTotal
   in groups, or show the header amounts once in the order group footer. LineTax is the line's share
   of tax (ROUND(LineTotal * TaxRate, 2)); summed over an order it can differ from OrderTax by a few
   cents because the official tax is rounded once on the subtotal. LineCost/LineMargin use the
   product's CURRENT cost (the schema does not keep historical cost). */
CREATE OR ALTER VIEW dbo.vw_SalesReport
AS
SELECT
    o.Id                                                    AS SalesOrderId,
    o.OrderNumber,
    o.OrderDate,
    CAST(o.OrderDate AS date)                               AS OrderDay,
    YEAR(o.OrderDate)                                       AS OrderYear,
    MONTH(o.OrderDate)                                      AS OrderMonth,
    o.Status,
    CASE o.Status WHEN N'Draft' THEN N'Borrador' WHEN N'Confirmed' THEN N'Confirmado'
                  WHEN N'Shipped' THEN N'Embarcado' WHEN N'Cancelled' THEN N'Cancelado' END AS StatusLabel,
    CAST(CASE WHEN o.Status IN (N'Confirmed', N'Shipped') THEN 1 ELSE 0 END AS bit) AS IsBilled,
    o.ConfirmedAt,
    o.ShippedAt,
    o.ExternalReference,
    w.Code                                                  AS WarehouseCode,
    w.Name                                                  AS WarehouseName,
    c.Id                                                    AS CustomerId,
    c.CustomerCode,
    c.Name                                                  AS CustomerName,
    c.Rfc                                                   AS CustomerRfc,
    c.City                                                  AS CustomerCity,
    c.State                                                 AS CustomerState,
    c.TaxZone,
    o.TaxRate,
    d.Id                                                    AS LineId,
    p.Id                                                    AS ProductId,
    p.ProductCode,
    p.ProductName,
    p.Category,
    p.Unit,
    d.Quantity,
    d.UnitPrice,
    d.LineTotal,
    CAST(ROUND(d.LineTotal * o.TaxRate, 2) AS decimal(18,2))            AS LineTax,
    CAST(ROUND(d.Quantity * p.Cost, 2) AS decimal(18,2))                AS LineCost,
    CAST(d.LineTotal - ROUND(d.Quantity * p.Cost, 2) AS decimal(18,2))  AS LineMargin,
    o.Subtotal                                              AS OrderSubtotal,
    o.Tax                                                   AS OrderTax,
    o.Total                                                 AS OrderTotal
FROM dbo.SalesOrders o
JOIN dbo.SalesOrderDetails d ON d.SalesOrderId = o.Id
JOIN dbo.Customers c         ON c.Id = o.CustomerId
JOIN dbo.Warehouses w        ON w.Id = o.WarehouseId
JOIN dbo.Products p          ON p.Id = d.ProductId;
GO
