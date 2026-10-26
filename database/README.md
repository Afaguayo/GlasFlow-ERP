# GlasFlow ERP — SQL Server scripts

T-SQL for the GlasFlow ERP database (SQL Server 2016 SP1 or later; tested on SQL Server 2022 Express).
The table design is described in [`docs/DATABASE_DESIGN.md`](../docs/DATABASE_DESIGN.md). Crystal Reports integration is covered in
[`docs/CRYSTAL_REPORTS.md`](../docs/CRYSTAL_REPORTS.md).

| # | File | What it does | Re-runnable |
|---|------|--------------|-------------|
| 01 | `01_schema.sql` | Creates the `GlasFlowERP` database, tables, keys, indexes and check constraints. Generated from the EF Core migration (`dotnet ef migrations script --idempotent`). | Yes (migration-history guarded) |
| 02 | `02_seed_data.sql` | Fictional demo data: the same warehouses, suppliers, customers and products as `DataSeeder.cs`, stock rows, and a small consistent history (10 sales orders, 4 production orders, movements). No users. | Yes (insert-if-missing by code; history only when `SalesOrders` is empty) |
| 03 | `03_stored_procedures.sql` | Transactional procedures `usp_*` that apply the C# business rules. | Yes (`CREATE OR ALTER`) |
| 04 | `04_views.sql` | Report views `vw_*` (the Crystal Reports / BI datasets). | Yes (`CREATE OR ALTER`) |
| 05 | `05_triggers.sql` | Audit triggers `trg_*` that write `AuditLogs` with `Source = 'DB_TRIGGER'`. | Yes (`CREATE OR ALTER`) |
| 06 | `06_reports.sql` | Read-only analytical queries (CTEs, window functions, ABC, turnover, reconciliation). | Yes (SELECT only) |

**Execution order:** 01 → 02 → 03 → 04 → 05 → 06. Script 02 must run before 05 if you don't want the seed's stock
updates to appear as trigger audit rows. Running it after 05 still works; the seed changes are then audited too.

Only `01_schema.sql` contains `USE GlasFlowERP`. Every other script runs against whatever database you
select with `-d` (or the SSMS database drop-down), so the same files work for a test copy.

## Automatic deployment by the API

When the API runs against SQL Server it does three things at start-up:

1. `Database.Migrate()` creates or updates the tables. This is the same schema as 01.
2. `SqlServerObjectsInstaller` reads `03_stored_procedures.sql`, `04_views.sql` and `05_triggers.sql` from the
   `database` folder next to the executable. It splits each file on lines that contain only `GO` and runs every batch.
   Batches made only of `USE` statements are skipped, so the objects always land in the API's own database.
   Because the scripts use `CREATE OR ALTER`, this runs safely on every start.
3. `DataSeeder` inserts the demo data and users if they are missing.

The trigger names are declared in the EF model (`GlasFlowDbContext`, `HasTrigger(...)`). EF must know a
table has triggers, because SQL Server rejects `OUTPUT` clauses on tables with triggers. If you rename a
trigger, rename it in the model as well.

So in normal use you don't run 03–05 by hand. Run them manually for a database the API doesn't manage, for CI,
or to inspect the objects. Scripts 02 and 06 are never run by the API.

## Running manually

### sqlcmd (Windows authentication)

```powershell
$srv = '.\SQLEXPRESS'          # or 'np:\\.\pipe\MSSQL$SQLEXPRESS\sql\query' for named pipes
sqlcmd -S $srv -E -C -b -i database\01_schema.sql
foreach ($f in '02_seed_data','03_stored_procedures','04_views','05_triggers') {
    sqlcmd -S $srv -E -C -b -d GlasFlowERP -i "database\$f.sql"
}
sqlcmd -S $srv -E -C -d GlasFlowERP -W -s "|" -i database\06_reports.sql   # analytical output
```

- `-E` Windows authentication (use `-U login -P ...` for a SQL login), `-C` trust the server certificate,
  `-b` exit with a non-zero code on the first error (important in scripts/CI), `-d` target database.
- The files are UTF-8 (Spanish text such as `Ciudad Juárez`). The go-based `sqlcmd` (v1.x) and SSMS
  read UTF-8 correctly. With the legacy ODBC `sqlcmd.exe`, add `-f 65001`.
- To build a disposable copy, replace `GlasFlowERP` with another name in a copy of 01 and use that name with `-d`.

### SSMS / Azure Data Studio

Open each file and pick the target database in the toolbar (not needed for 01). Then execute with F5.
`GO` is understood natively. In 06, highlight a single query (R1…R10) and press F5 to run only that one.

## Quick checks after deployment

```sql
SELECT type_desc, name FROM sys.objects WHERE type IN ('P','V','TR') AND is_ms_shipped = 0 ORDER BY type_desc, name;
-- 10 procedures, 7 views, 4 triggers

EXEC dbo.usp_GetInventorySummary;                 -- stock per product/warehouse + totals by category
EXEC dbo.usp_RunLowStockCheck;                    -- creates/resolves LowStock notifications (idempotent)
SELECT * FROM dbo.vw_LowStockProducts;
SELECT TOP 20 * FROM dbo.AuditLogs WHERE Source IN ('DB_PROC','DB_TRIGGER') ORDER BY Id DESC;
```

### Procedure examples

```sql
DECLARE @id int;
EXEC dbo.usp_CreateSalesOrder
     @CustomerId = 1, @WarehouseId = 1,
     @Lines = N'[{"productId":1,"quantity":10},{"productId":13,"quantity":24,"unitPrice":139.90}]',
     @Notes = N'Pedido telefónico', @ExternalReference = N'PORTAL-000123', @UserName = N'operador',
     @SalesOrderId = @id OUTPUT;
EXEC dbo.usp_ConfirmSalesOrder @SalesOrderId = @id, @UserName = N'operador';   -- reserves stock, all or nothing
EXEC dbo.usp_ShipSalesOrder    @SalesOrderId = @id, @UserName = N'operador';   -- deducts reserved stock

EXEC dbo.usp_UpdateInventory @ProductId = 1, @WarehouseId = 1, @Delta = -2.5, @Reason = N'Rotura', @UserName = N'operador';

EXEC dbo.usp_StartProductionOrder    @ProductionOrderId = 4;
EXEC dbo.usp_RegisterProduction      @ProductionOrderId = 4, @Quantity = 20;
EXEC dbo.usp_CompleteProductionOrder @ProductionOrderId = 4, @FinalQuantity = 40;
```

Errors are raised with `THROW`: **50001** = not found, **50002** = business rule (wrong status, not enough stock,
over-production, negative stock…), **50003** = duplicate (`ExternalReference`). The messages match the API's messages.

## Notes and limitations

- The procedures re-implement the C# rules for database-side clients (SSMS, integrations, jobs). The API
  does **not** call them; it uses EF Core with the same rules. Both write to the same tables and audit
  trail. Procedure rows use `Source = 'DB_PROC'` and API rows use `'APP'`.
- The procedures always refuse to go below zero free stock. They ignore the API's `Inventory:AllowNegativeStock` option.
- Every procedure stores the acting user in `SESSION_CONTEXT(N'UserName')` so trigger rows carry that user name.
  The value stays for the rest of the session. Rows written through the API show the SQL login, because the
  API doesn't set the session context.
- `01_schema.sql` line 19 begins with an invisible byte-order mark (U+FEFF) left over from the EF script
  generation. The go-based `sqlcmd` reports `Incorrect syntax near '﻿'` for that batch. Remove the character
  (or regenerate the file) before running 01 with sqlcmd. The API is not affected because it uses migrations.
