/* =====================================================================================
   GlasFlow ERP — 06_reports.sql
   Ad-hoc analytical queries against the real schema (read-only: SELECTs only, nothing is changed).
   Each query is its own batch and can be run alone (select it in SSMS and press F5).

   Execution order:  01_schema.sql -> 02_seed_data.sql -> 03_stored_procedures.sql
                     -> 04_views.sql -> 05_triggers.sql -> 06_reports.sql (ad-hoc queries)

   Run against the target database (no USE statement on purpose):
     sqlcmd -S .\SQLEXPRESS -E -C -b -d GlasFlowERP -i database\06_reports.sql

   Techniques shown
     R1  JOIN + GROUP BY + HAVING + RANK + share of total ............ top customers
     R2  recursive CTE (month calendar) + LAG + SUM OVER ............. monthly sales, MoM, YTD
     R3  multi-step CTE + running SUM OVER ........................... ABC classification of products
     R4  multi-step CTE + conditional aggregates ..................... inventory turnover / days of supply
     R5  window running balance + reconciliation ..................... movements vs Inventory.QuantityAvailable
     R6  DATEDIFF + PERCENTILE_CONT + AVG OVER ....................... order fulfilment lead time
     R7  conditional aggregates + subquery ........................... production on-time rate
     R8  ROW_NUMBER per partition .................................... best-selling product per category
     R9  correlated subqueries / NOT EXISTS .......................... dormant customers, above-average orders
     R10 HAVING on a ratio ........................................... credit exposure

   Shared definitions: billed sales = Status IN ('Confirmed','Shipped'); free stock =
   QuantityAvailable - QuantityReserved; Reservation/ReservationRelease do not move on-hand stock.
   All dates are UTC.
   ===================================================================================== */
SET NOCOUNT ON;
GO

/* R1 — Top customers by billed revenue.
   RANK() leaves gaps on ties; ShareOfTotal is each customer's percentage of all billed sales.
   HAVING keeps only customers with at least one billed order. */
SELECT
    RANK() OVER (ORDER BY SUM(o.Total) DESC)                    AS SalesRank,
    c.CustomerCode,
    c.Name                                                      AS CustomerName,
    c.TaxZone,
    COUNT(*)                                                    AS BilledOrders,
    SUM(o.Subtotal)                                             AS Subtotal,
    SUM(o.Total)                                                AS Total,
    CAST(AVG(o.Total) AS decimal(18,2))                         AS AverageOrder,
    CAST(100.0 * SUM(o.Total) / SUM(SUM(o.Total)) OVER () AS decimal(5,2)) AS ShareOfTotalPct
FROM dbo.SalesOrders o
JOIN dbo.Customers c ON c.Id = o.CustomerId
WHERE o.Status IN (N'Confirmed', N'Shipped')
GROUP BY c.CustomerCode, c.Name, c.TaxZone
HAVING COUNT(*) >= 1
ORDER BY SalesRank, c.CustomerCode;
GO

/* R2 — Monthly billed sales for the last 12 months, including months without sales.
   A recursive CTE builds the month calendar (so empty months show 0 instead of disappearing),
   LAG() gives the previous month for month-over-month growth, and SUM() OVER (PARTITION BY year)
   gives the year-to-date running total. */
DECLARE @FirstMonth date = DATEADD(month, -11, DATEFROMPARTS(YEAR(SYSUTCDATETIME()), MONTH(SYSUTCDATETIME()), 1));

WITH Months AS (
    SELECT @FirstMonth AS MonthStart, 1 AS n
    UNION ALL
    SELECT DATEADD(month, 1, MonthStart), n + 1 FROM Months WHERE n < 12
),
Sales AS (
    SELECT DATEFROMPARTS(YEAR(OrderDate), MONTH(OrderDate), 1) AS MonthStart,
           COUNT(*) AS Orders, SUM(Subtotal) AS Subtotal, SUM(Total) AS Total
    FROM dbo.SalesOrders
    WHERE Status IN (N'Confirmed', N'Shipped') AND OrderDate >= @FirstMonth
    GROUP BY DATEFROMPARTS(YEAR(OrderDate), MONTH(OrderDate), 1)
),
Joined AS (
    SELECT m.MonthStart, ISNULL(s.Orders, 0) AS Orders, ISNULL(s.Subtotal, 0) AS Subtotal, ISNULL(s.Total, 0) AS Total
    FROM Months m LEFT JOIN Sales s ON s.MonthStart = m.MonthStart
)
SELECT
    CONVERT(char(7), MonthStart, 126)                                           AS YearMonth,
    Orders,
    Subtotal,
    Total,
    LAG(Total) OVER (ORDER BY MonthStart)                                       AS PreviousMonthTotal,
    CAST(100.0 * (Total - LAG(Total) OVER (ORDER BY MonthStart))
         / NULLIF(LAG(Total) OVER (ORDER BY MonthStart), 0) AS decimal(9,1))     AS MoMGrowthPct,
    SUM(Total) OVER (PARTITION BY YEAR(MonthStart) ORDER BY MonthStart
                     ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)          AS YearToDateTotal,
    CAST(AVG(Total) OVER (ORDER BY MonthStart ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS decimal(18,2)) AS Moving3MonthAvg
FROM Joined
ORDER BY MonthStart
OPTION (MAXRECURSION 12);
GO

/* R3 — ABC classification of products by billed revenue (Pareto).
   Step 1 revenue per product, step 2 cumulative share ordered by revenue, step 3 class:
   A = products that make up the first 80% of revenue, B = next 15%, C = last 5% and unsold items. */
WITH Revenue AS (
    SELECT p.Id AS ProductId, p.ProductCode, p.ProductName, p.Category,
           SUM(CASE WHEN o.Status IN (N'Confirmed', N'Shipped') THEN d.LineTotal ELSE 0 END) AS Revenue
    FROM dbo.Products p
    LEFT JOIN dbo.SalesOrderDetails d ON d.ProductId = p.Id
    LEFT JOIN dbo.SalesOrders o       ON o.Id = d.SalesOrderId
    WHERE p.Active = 1
    GROUP BY p.Id, p.ProductCode, p.ProductName, p.Category
),
Cumulative AS (
    SELECT *,
           SUM(Revenue) OVER (ORDER BY Revenue DESC, ProductCode ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS CumRevenue,
           SUM(Revenue) OVER () AS TotalRevenue
    FROM Revenue
)
SELECT
    ROW_NUMBER() OVER (ORDER BY Revenue DESC, ProductCode)          AS RevenueRank,
    ProductCode, ProductName, Category, Revenue,
    CAST(100.0 * Revenue    / NULLIF(TotalRevenue, 0) AS decimal(5,2)) AS SharePct,
    CAST(100.0 * CumRevenue / NULLIF(TotalRevenue, 0) AS decimal(5,2)) AS CumulativeSharePct,
    CASE WHEN Revenue = 0 THEN 'C'
         WHEN CumRevenue - Revenue < 0.80 * TotalRevenue THEN 'A'   -- product starts inside the first 80%
         WHEN CumRevenue - Revenue < 0.95 * TotalRevenue THEN 'B'
         ELSE 'C' END                                               AS AbcClass
FROM Cumulative
ORDER BY RevenueRank;
GO

/* R4 — Inventory turnover and days of supply per product (all warehouses), last 365 days.
   Units shipped come from SaleShipment movements; average on-hand = (on-hand 365 days ago + on-hand now) / 2,
   where on-hand 365 days ago is rebuilt from the movement ledger. Turnover = shipped / average on-hand;
   DaysOfSupply = free stock / average daily shipments. */
DECLARE @From datetime2 = DATEADD(day, -365, SYSUTCDATETIME());

WITH Ledger AS (
    SELECT m.ProductId,
           SUM(CASE WHEN m.[Timestamp] < @From AND m.MovementType NOT IN (N'Reservation', N'ReservationRelease') THEN m.Quantity ELSE 0 END) AS OnHandAtStart,
           SUM(CASE WHEN m.[Timestamp] >= @From AND m.MovementType = N'SaleShipment' THEN -m.Quantity ELSE 0 END)                           AS UnitsShipped,
           SUM(CASE WHEN m.[Timestamp] >= @From AND m.MovementType = N'ProductionReceipt' THEN m.Quantity ELSE 0 END)                       AS UnitsProduced
    FROM dbo.InventoryMovements m
    GROUP BY m.ProductId
),
Stock AS (
    SELECT ProductId, SUM(QuantityAvailable) AS OnHandNow, SUM(QuantityAvailable - QuantityReserved) AS FreeNow
    FROM dbo.Inventory GROUP BY ProductId
),
Metrics AS (
    SELECT p.ProductCode, p.ProductName, p.Category, p.Unit, p.Cost,
           ISNULL(l.OnHandAtStart, 0) AS OnHandAtStart, s.OnHandNow, s.FreeNow,
           ISNULL(l.UnitsShipped, 0) AS UnitsShipped, ISNULL(l.UnitsProduced, 0) AS UnitsProduced,
           (ISNULL(l.OnHandAtStart, 0) + s.OnHandNow) / 2.0 AS AvgOnHand
    FROM dbo.Products p
    JOIN Stock s       ON s.ProductId = p.Id
    LEFT JOIN Ledger l ON l.ProductId = p.Id
    WHERE p.Active = 1
)
SELECT ProductCode, ProductName, Category, Unit, OnHandAtStart, OnHandNow, UnitsShipped, UnitsProduced,
       CAST(AvgOnHand AS decimal(18,1))                                              AS AvgOnHand,
       CAST(UnitsShipped / NULLIF(AvgOnHand, 0) AS decimal(9,2))                     AS TurnoverRatio,
       CAST(ROUND(UnitsShipped * Cost, 2) AS decimal(18,2))                          AS CostOfGoodsShipped,
       CAST(FreeNow / NULLIF(UnitsShipped / 365.0, 0) AS decimal(9,0))               AS DaysOfSupply
FROM Metrics
ORDER BY TurnoverRatio DESC, ProductCode;
GO

/* R5 — Running on-hand balance per product x warehouse rebuilt from the movement ledger, and the
   reconciliation against dbo.Inventory. The first result is the ledger of one product with a
   running balance; the second lists every stock row where ledger and table disagree
   (expected: no rows — every stock change writes its movement in the same transaction). */
DECLARE @ProductCode nvarchar(30) = N'TMP-10MM';

SELECT w.Code AS WarehouseCode, m.[Timestamp], m.MovementType, m.Reference, m.Quantity,
       SUM(CASE WHEN m.MovementType IN (N'Reservation', N'ReservationRelease') THEN 0 ELSE m.Quantity END)
           OVER (PARTITION BY m.WarehouseId ORDER BY m.[Timestamp], m.Id
                 ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW) AS RunningOnHand
FROM dbo.InventoryMovements m
JOIN dbo.Products p   ON p.Id = m.ProductId
JOIN dbo.Warehouses w ON w.Id = m.WarehouseId
WHERE p.ProductCode = @ProductCode
ORDER BY w.Code, m.[Timestamp], m.Id;

WITH LedgerBalance AS (
    SELECT ProductId, WarehouseId,
           SUM(CASE WHEN MovementType IN (N'Reservation', N'ReservationRelease') THEN 0 ELSE Quantity END) AS LedgerOnHand,
           SUM(CASE WHEN MovementType = N'Reservation' THEN Quantity
                    WHEN MovementType = N'ReservationRelease' THEN Quantity          -- stored negative
                    WHEN MovementType = N'SaleShipment' THEN Quantity                -- a shipment consumes its reservation
                    ELSE 0 END) AS LedgerReserved
    FROM dbo.InventoryMovements
    GROUP BY ProductId, WarehouseId
)
SELECT p.ProductCode, w.Code AS WarehouseCode,
       i.QuantityAvailable, ISNULL(l.LedgerOnHand, 0) AS LedgerOnHand,
       i.QuantityAvailable - ISNULL(l.LedgerOnHand, 0) AS OnHandDifference,
       i.QuantityReserved, ISNULL(l.LedgerReserved, 0) AS LedgerReserved
FROM dbo.Inventory i
JOIN dbo.Products p   ON p.Id = i.ProductId
JOIN dbo.Warehouses w ON w.Id = i.WarehouseId
LEFT JOIN LedgerBalance l ON l.ProductId = i.ProductId AND l.WarehouseId = i.WarehouseId
WHERE i.QuantityAvailable <> ISNULL(l.LedgerOnHand, 0)
ORDER BY p.ProductCode, w.Code;
GO

/* R6 — Order fulfilment lead time (shipped orders): hours from order to confirmation and days from
   order to shipment, per warehouse, with median and 90th percentile (PERCENTILE_CONT is a window
   function, hence DISTINCT) and the company-wide average for comparison. */
WITH Shipped AS (
    SELECT w.Code AS WarehouseCode, o.OrderNumber,
           DATEDIFF(minute, o.OrderDate, o.ConfirmedAt) / 60.0 AS HoursToConfirm,
           DATEDIFF(minute, o.OrderDate, o.ShippedAt) / 1440.0 AS DaysToShip
    FROM dbo.SalesOrders o
    JOIN dbo.Warehouses w ON w.Id = o.WarehouseId
    WHERE o.Status = N'Shipped' AND o.ConfirmedAt IS NOT NULL AND o.ShippedAt IS NOT NULL
)
SELECT DISTINCT
    WarehouseCode,
    COUNT(*) OVER (PARTITION BY WarehouseCode)                                                       AS ShippedOrders,
    CAST(AVG(HoursToConfirm) OVER (PARTITION BY WarehouseCode) AS decimal(9,1))                      AS AvgHoursToConfirm,
    CAST(AVG(DaysToShip) OVER (PARTITION BY WarehouseCode) AS decimal(9,2))                          AS AvgDaysToShip,
    CAST(PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY DaysToShip) OVER (PARTITION BY WarehouseCode) AS decimal(9,2)) AS MedianDaysToShip,
    CAST(PERCENTILE_CONT(0.9) WITHIN GROUP (ORDER BY DaysToShip) OVER (PARTITION BY WarehouseCode) AS decimal(9,2)) AS P90DaysToShip,
    CAST(AVG(DaysToShip) OVER () AS decimal(9,2))                                                    AS CompanyAvgDaysToShip
FROM Shipped
ORDER BY WarehouseCode;
GO

/* R7 — Production on-time rate: completed orders finished on or before their due date, per product,
   plus the overall rate as a scalar subquery, and the current backlog (planned + in progress). */
SELECT
    p.ProductCode,
    p.ProductName,
    COUNT(*)                                                                      AS CompletedOrders,
    SUM(CASE WHEN CAST(po.CompletionDate AS date) <= CAST(po.DueDate AS date) THEN 1 ELSE 0 END) AS OnTime,
    CAST(100.0 * SUM(CASE WHEN CAST(po.CompletionDate AS date) <= CAST(po.DueDate AS date) THEN 1 ELSE 0 END)
         / COUNT(*) AS decimal(5,1))                                              AS OnTimePct,
    CAST(AVG(DATEDIFF(day, po.StartDate, po.CompletionDate) * 1.0) AS decimal(9,1)) AS AvgDaysInProduction,
    CAST(100.0 * SUM(po.QuantityCompleted) / SUM(po.QuantityPlanned) AS decimal(5,1)) AS YieldPct,
    (SELECT CAST(100.0 * SUM(CASE WHEN CAST(x.CompletionDate AS date) <= CAST(x.DueDate AS date) THEN 1 ELSE 0 END)
                 / NULLIF(COUNT(*), 0) AS decimal(5,1))
     FROM dbo.ProductionOrders x WHERE x.Status = N'Completed' AND x.DueDate IS NOT NULL) AS CompanyOnTimePct
FROM dbo.ProductionOrders po
JOIN dbo.Products p ON p.Id = po.ProductId
WHERE po.Status = N'Completed' AND po.DueDate IS NOT NULL
GROUP BY p.ProductCode, p.ProductName
ORDER BY OnTimePct DESC, p.ProductCode;

SELECT po.Status, COUNT(*) AS Orders, SUM(po.QuantityPlanned - po.QuantityCompleted) AS PendingQuantity,
       SUM(CASE WHEN po.DueDate < CAST(SYSUTCDATETIME() AS date) THEN 1 ELSE 0 END) AS Overdue
FROM dbo.ProductionOrders po
WHERE po.Status IN (N'Planned', N'InProgress')
GROUP BY po.Status;
GO

/* R8 — Best-selling product per category (billed quantity), using ROW_NUMBER() per partition. */
WITH ByProduct AS (
    SELECT p.Category, p.ProductCode, p.ProductName, p.Unit,
           SUM(d.Quantity) AS QuantitySold, SUM(d.LineTotal) AS Revenue,
           ROW_NUMBER() OVER (PARTITION BY p.Category ORDER BY SUM(d.Quantity) DESC, p.ProductCode) AS rn
    FROM dbo.SalesOrderDetails d
    JOIN dbo.SalesOrders o ON o.Id = d.SalesOrderId
    JOIN dbo.Products p    ON p.Id = d.ProductId
    WHERE o.Status IN (N'Confirmed', N'Shipped')
    GROUP BY p.Category, p.ProductCode, p.ProductName, p.Unit
)
SELECT Category, ProductCode, ProductName, Unit, QuantitySold, Revenue
FROM ByProduct
WHERE rn = 1
ORDER BY Revenue DESC;
GO

/* R9 — Subqueries.
   a) Dormant customers: active customers with no billed order in the last 90 days (NOT EXISTS),
      with their last order date from a correlated subquery.
   b) Orders above the average billed order value (scalar subquery in WHERE). */
SELECT c.CustomerCode, c.Name AS CustomerName, c.City,
       (SELECT MAX(o.OrderDate) FROM dbo.SalesOrders o
        WHERE o.CustomerId = c.Id AND o.Status IN (N'Confirmed', N'Shipped')) AS LastBilledOrder
FROM dbo.Customers c
WHERE c.Active = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.SalesOrders o
                  WHERE o.CustomerId = c.Id AND o.Status IN (N'Confirmed', N'Shipped')
                    AND o.OrderDate >= DATEADD(day, -90, SYSUTCDATETIME()))
ORDER BY LastBilledOrder DESC, c.CustomerCode;

SELECT o.OrderNumber, c.Name AS CustomerName, o.Status, o.Total,
       CAST(o.Total - (SELECT AVG(Total) FROM dbo.SalesOrders WHERE Status IN (N'Confirmed', N'Shipped')) AS decimal(18,2)) AS AboveAverageBy
FROM dbo.SalesOrders o
JOIN dbo.Customers c ON c.Id = o.CustomerId
WHERE o.Status IN (N'Confirmed', N'Shipped')
  AND o.Total > (SELECT AVG(Total) FROM dbo.SalesOrders WHERE Status IN (N'Confirmed', N'Shipped'))
ORDER BY o.Total DESC;
GO

/* R10 — Credit exposure: customers whose confirmed-but-unshipped orders use 10% or more of their
   credit limit (HAVING on a ratio of aggregates). */
SELECT c.CustomerCode, c.Name AS CustomerName, c.CreditLimit,
       SUM(o.Total) AS OpenOrdersTotal,
       CAST(100.0 * SUM(o.Total) / NULLIF(c.CreditLimit, 0) AS decimal(5,1)) AS CreditUsedPct
FROM dbo.Customers c
JOIN dbo.SalesOrders o ON o.CustomerId = c.Id AND o.Status = N'Confirmed'
GROUP BY c.CustomerCode, c.Name, c.CreditLimit
HAVING SUM(o.Total) >= 0.10 * c.CreditLimit
ORDER BY CreditUsedPct DESC;
GO
