# Crystal Reports integration

> **Status, stated plainly.** No Crystal Reports runtime or designer is installed in the environment this project was built in.
> **No `.rpt` file was created, and nothing was tested inside Crystal Reports.** This document is split into two parts:
> what **is implemented** (the SQL Server datasets a Crystal report would bind to, and the web/CSV reports that
> exist today), and what is **documented only** (how to connect Crystal Reports and the specifications of three reports).

| Item | Status |
|---|---|
| Report views in SQL Server (`vw_SalesReport`, `vw_CurrentInventory`, `vw_LowStockProducts`, `vw_ProductionStatus`, `vw_SalesByMonth`, `vw_CustomerSalesSummary`, `vw_InventoryMovementHistory`) | **Implemented.** `database/04_views.sql`, deployed by the API at start-up and verified with SELECTs and the API endpoint |
| Read-only view endpoint `GET /api/reports/views/{name}` (whitelisted names, `?top=` up to 5000) | **Implemented** |
| JSON report endpoints `GET /api/reports/{sales,inventory,low-stock,production}` | **Implemented** |
| CSV exports `GET /api/reports/{sales,inventory,low-stock,production}/csv` (UTF-8 with BOM, opens correctly in Excel) | **Implemented** |
| Printable web layouts: *Reportes* page in the web UI with filters, totals, **Exportar CSV** and **Imprimir** (print stylesheet, letter landscape) | **Implemented** |
| `usp_GetInventorySummary` (two result sets: detail and totals by category) | **Implemented** |
| Crystal Reports connection (OLE DB / ODBC) | **Documented only** |
| The three `.rpt` report designs below | **Documented only.** These are specifications, not files |
| Embedding the CR viewer in the WPF desktop app | **Documented only.** Not built |

## 1. Connecting Crystal Reports to SQL Server *(documented only)*

Supported designers: **SAP Crystal Reports for Visual Studio** (the free SP3x runtime for .NET Framework 4.x) or
**Crystal Reports 2020** (standalone designer). Note that the CR for VS runtime targets .NET Framework, not .NET 10.
To embed it in the desktop app you would need a separate .NET Framework host or a reporting micro-service.
Designing reports in CR 2020 against the views has no such restriction.

1. **Driver.** Install *Microsoft ODBC Driver 18 for SQL Server* (or 17). The alternative is *Microsoft OLE DB Driver
   for SQL Server* (MSOLEDBSQL). CR 2020 is 64-bit, so create a 64-bit ODBC DSN. CR for VS in a 32-bit process needs a 32-bit DSN.
2. **Login.** Choose one:
   - Windows authentication (Trusted_Connection), when the report runs as a domain user; or
   - a dedicated **read-only SQL login** that can only read the views:
     ```sql
     CREATE LOGIN glasflow_reports WITH PASSWORD = '<strong password>', CHECK_POLICY = ON;
     USE GlasFlowERP;
     CREATE USER glasflow_reports FOR LOGIN glasflow_reports;
     GRANT SELECT ON dbo.vw_SalesReport TO glasflow_reports;
     GRANT SELECT ON dbo.vw_CurrentInventory TO glasflow_reports;
     GRANT SELECT ON dbo.vw_LowStockProducts TO glasflow_reports;
     GRANT SELECT ON dbo.vw_ProductionStatus TO glasflow_reports;
     GRANT SELECT ON dbo.vw_SalesByMonth TO glasflow_reports;
     GRANT SELECT ON dbo.vw_CustomerSalesSummary TO glasflow_reports;
     GRANT SELECT ON dbo.vw_InventoryMovementHistory TO glasflow_reports;
     ```
     Ownership chaining lets the views read the base tables without granting the tables.
3. **Encryption.** ODBC Driver 18 encrypts by default. Against SQL Express with a self-signed certificate, set
   *Trust server certificate = Yes* (connection string `TrustServerCertificate=yes`), or install a proper certificate.
4. **In the designer:** *Database Expert → Create New Connection → ODBC (RDO)* or *OLE DB (ADO)* → choose the server
   (`.\SQLEXPRESS`) and the database `GlasFlowERP` → expand `dbo` → **Views** → add the view. Bind reports to the views,
   not to the tables. The views keep stable column names and already apply the business rules (billed = Confirmed +
   Shipped, free stock, low-stock flag). A schema change then doesn't break the reports.
5. **Pass parameters to SQL.** For date ranges, use *Record Selection* on `{vw_SalesReport.OrderDate}`. CR pushes a
   simple record selection down as a `WHERE` clause, so filtering happens on the server.
6. **Dates are UTC.** Show local time (America/Chihuahua, UTC−6, no DST since 2022) with a formula, e.g.
   `DateAdd("h", -6, {vw_SalesReport.OrderDate})`.

## 2. Report specifications *(documented only)*

### Report A — Sales by period, grouped by customer (`GF_Ventas_Periodo.rpt`)

- **Data source:** `dbo.vw_SalesReport` (one row per order line).
- **Parameters:** `?FechaInicio` (Date), `?FechaFin` (Date), `?SoloFacturado` (Boolean, default True),
  `?Almacen` (String, multi-value, values `CUU-01`, `CJS-01`, default all).
- **Record selection:**
  `{vw_SalesReport.OrderDay} >= {?FechaInicio} and {vw_SalesReport.OrderDay} <= {?FechaFin}
   and ({?SoloFacturado} = false or {vw_SalesReport.IsBilled} = true)
   and {vw_SalesReport.WarehouseCode} in {?Almacen}`
- **Groups:**
  1. `CustomerName` (header shows `CustomerCode`, `CustomerRfc`, `CustomerCity`, `TaxZone`)
  2. `OrderNumber` (header shows `OrderDate`, `StatusLabel`, `WarehouseCode`)
- **Details:** `ProductCode`, `ProductName`, `Unit`, `Quantity`, `UnitPrice`, `LineTotal`.
- **Order group footer:** `OrderSubtotal`, `OrderTax`, `OrderTotal`, printed once per order. These come from the header
  and repeat on each line, so do **not** sum them in the detail section.
- **Formulas:**
  - `@TotalPedido` = `Maximum({vw_SalesReport.OrderTotal}, {vw_SalesReport.OrderNumber})`
  - `@TotalCliente` = `Sum({@TotalPedido}, {vw_SalesReport.CustomerName})`. Use a running total of `OrderTotal`
    that evaluates *on change of `OrderNumber`* and resets on change of `CustomerName`.
  - `@MargenPct` = `if Sum({vw_SalesReport.LineTotal}, {vw_SalesReport.CustomerName}) = 0 then 0 else
     100 * Sum({vw_SalesReport.LineMargin}, {vw_SalesReport.CustomerName}) / Sum({vw_SalesReport.LineTotal}, {vw_SalesReport.CustomerName})`
    (margin is computed at the product's current cost)
  - `@IVA` = `ToText({vw_SalesReport.TaxRate} * 100, 0) & "%"`
- **Customer group footer:** number of orders (`DistinctCount(OrderNumber)`), subtotal (`Sum(LineTotal)`), the
  `@TotalCliente` running total, `@MargenPct`.
- **Report footer:** grand totals, plus a chart (bar, total per customer).
- **Sort:** customer group by total descending (*Group Sort Expert → Top N / sort by sum*). Orders by `OrderDate`.
- **Cross-check with the implemented API:** `GET /api/reports/sales?from=&to=` returns the same orders and its
  `BilledTotal` summary equals this report's grand total when `?SoloFacturado` = True.

### Report B — Inventory valuation by category and warehouse, with low-stock highlight (`GF_Valuacion_Inventario.rpt`)

- **Data source:** `dbo.vw_CurrentInventory`. An optional subreport uses `dbo.vw_LowStockProducts` for the reorder list.
- **Parameters:** `?Almacen` (String, multi-value, default all), `?Categoria` (String, multi-value, default all),
  `?SoloBajoMinimo` (Boolean, default False).
- **Record selection:** `{vw_CurrentInventory.WarehouseCode} in {?Almacen} and {vw_CurrentInventory.Category} in {?Categoria}
   and ({?SoloBajoMinimo} = false or {vw_CurrentInventory.IsLowStock} = true)`
- **Groups:** 1. `WarehouseCode` (header shows `WarehouseName`), 2. `Category`.
- **Details:** `ProductCode`, `ProductName`, `Unit`, `QuantityAvailable`, `QuantityReserved`, `QuantityFree`,
  `MinimumStock`, `UnitCost`, `StockValue`, `StockStatus`.
- **Conditional formatting (the highlight):** details section background
  `if {vw_CurrentInventory.QuantityFree} <= 0 then crRed else if {vw_CurrentInventory.IsLowStock} then RGB(255, 235, 156) else crNoColor`.
  Font of `QuantityFree` is bold when `IsLowStock`.
- **Formulas:**
  - `@CoberturaPct` = `if {vw_CurrentInventory.MinimumStock} = 0 then 0 else 100 * {vw_CurrentInventory.QuantityFree} / {vw_CurrentInventory.MinimumStock}`
  - `@ValorReservado` = `{vw_CurrentInventory.QuantityReserved} * {vw_CurrentInventory.UnitCost}`
- **Group footers:** `Sum(StockValue)`, `Sum(@ValorReservado)`, `Count` of lines, a low-stock count
  (`Sum(if {vw_CurrentInventory.IsLowStock} then 1 else 0)`), and the category's share of the warehouse value.
- **Report footer:** total inventory value, number of low-stock lines, and a pie chart of value by category.
- **Sort:** category, then `StockValue` descending.
- **Cross-check:** `GET /api/reports/inventory` returns `InventoryValue`, `LowStockLines` and `Lines`. Its second
  result set matches `EXEC dbo.usp_GetInventorySummary`.

### Report C — Production status (`GF_Estado_Produccion.rpt`)

- **Data source:** `dbo.vw_ProductionStatus`.
- **Parameters:** `?Estatus` (String, multi-value: `Planned`, `InProgress`, `Completed`, `Cancelled`; default the first
  three), `?CreadaDesde` / `?CreadaHasta` (Date).
- **Record selection:** `{vw_ProductionStatus.Status} in {?Estatus} and Date({vw_ProductionStatus.CreatedUtc}) in {?CreadaDesde} to {?CreadaHasta}`
- **Groups:** 1. `StatusLabel`, in a specified order (En proceso, Planeada, Terminada, Cancelada), 2. `WarehouseCode`.
- **Details:** `ProductionNumber`, `ProductCode`, `ProductName`, `QuantityPlanned`, `QuantityCompleted`,
  `QuantityPending`, `ProgressPercent` (shown as a bar: a box whose width depends on the percentage, or a
  *Gauge* chart), `StartDate`, `DueDate`, `CompletionDate`, `DaysLateAtCompletion`.
- **Conditional formatting:** `DueDate` red when `{vw_ProductionStatus.IsOverdue}`. `CompletionDate` green or red by
  `{vw_ProductionStatus.CompletedOnTime}`.
- **Formulas:**
  - `@ATiempoPct` (report footer) = `100 * Sum(if {vw_ProductionStatus.CompletedOnTime} then 1 else 0) /
     Sum(if {vw_ProductionStatus.Status} = "Completed" then 1 else 0)`
  - `@AvanceGrupo` = `100 * Sum({vw_ProductionStatus.QuantityCompleted}, {vw_ProductionStatus.StatusLabel}) / Sum({vw_ProductionStatus.QuantityPlanned}, {vw_ProductionStatus.StatusLabel})`
- **Group footers:** order count, planned / completed / pending quantity, `@AvanceGrupo`.
- **Sort:** `DueDate` ascending, so the most urgent orders come first.
- **Cross-check:** `GET /api/reports/production` and `06_reports.sql` R7 (on-time rate).

## 3. What exists today instead *(implemented)*

- **Web Reports page** (web UI menu *Reportes*, route `reportes`). Sales, inventory, low-stock and production tabs with filters,
  summary totals, **Exportar CSV** and **Imprimir**. The print stylesheet lays the table out on letter landscape
  without the navigation. This is how the demo produces paper and PDF output (browser → *Print to PDF*).
- **JSON:** `GET /api/reports/sales?from=&to=&status=`, `/inventory?warehouseId=&category=`, `/low-stock?warehouseId=`,
  `/production?from=&to=&status=`. Each returns `{ title, generatedAt, from, to, rows, totals }`.
- **CSV:** the same routes with `/csv` appended. UTF-8 with BOM, invariant number format.
- **Raw view data:** `GET /api/reports/views` lists the view names. `GET /api/reports/views/{name}?top=500` returns the
  rows of a whitelisted view as JSON, the same dataset Crystal Reports would read. Any tool that consumes JSON or
  CSV (Excel Power Query, Power BI, SSRS via a shared dataset) can use these as an alternative to Crystal Reports.

## 4. Turning the documentation into real reports

1. Install CR 2020 (or CR for VS) and ODBC Driver 18 on a Windows machine with access to the database.
2. Create the read-only login (section 1).
3. Build reports A–C from the specifications above against the views, and save them under `reports/`.
4. Check every report total against the matching `/api/reports/...` summary.
5. Optional: host the CR runtime in a small .NET Framework service that renders `.rpt` to PDF, and call it from the API.
