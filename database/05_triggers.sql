/* =====================================================================================
   GlasFlow ERP — 05_triggers.sql
   Database-level audit triggers. They record changes no matter who makes them: the API (EF Core),
   the stored procedures in 03, SSMS, or an integration writing directly to the tables.

   Execution order:  01_schema.sql -> 02_seed_data.sql -> 03_stored_procedures.sql
                     -> 04_views.sql -> 05_triggers.sql -> 06_reports.sql (ad-hoc queries)

   No USE statement: run against the target database with sqlcmd -d GlasFlowERP.
   The API applies this file on every start-up (CREATE OR ALTER = idempotent).

   The trigger NAMES are part of the EF Core model (GlasFlowDbContext: HasTrigger(...)). EF must
   know a table has triggers, because SQL Server rejects INSERT/UPDATE ... OUTPUT (without INTO) on
   such tables; with HasTrigger EF reads generated values and row counts another way.
   Renaming a trigger here requires renaming it in GlasFlowDbContext too.

   Rules every trigger follows
   - AuditLogs rows with Source = 'DB_TRIGGER'.
   - UserName = SESSION_CONTEXT(N'UserName') when the caller set it (the procedures in 03 do),
     otherwise the SQL login (SUSER_SNAME()) — for the API that is its service/Windows account.
   - Set-based: inserted/deleted may hold many rows (one UPDATE can touch the whole table).
   - SET NOCOUNT ON, so no extra "rows affected" messages reach the client; @@ROWCOUNT seen by the
     caller after the statement is still the statement's own count (EF relies on it).
   - Never break the application's write: SET XACT_ABORT OFF + TRY/CATCH, and all text is cut to the
     column length with LEFT(). If writing the audit row fails, the error is swallowed and the
     business write commits (verified: the outer transaction stays committable, XACT_STATE() = 1).
     Losing one audit row is preferred to rejecting a sale.
   ===================================================================================== */

/* Stock quantity changes on dbo.Inventory (old -> new for available and reserved). */
CREATE OR ALTER TRIGGER dbo.trg_Inventory_Audit
ON dbo.Inventory
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF NOT (UPDATE(QuantityAvailable) OR UPDATE(QuantityReserved)) RETURN;

    BEGIN TRY
        DECLARE @User nvarchar(50) = LEFT(COALESCE(CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
        DECLARE @Now datetime2 = SYSUTCDATETIME();

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        SELECT @Now, @User, N'StockChanged', N'Inventory', CAST(i.Id AS nvarchar(60)),
               CONCAT(N'Product ', p.ProductCode, N' (', i.ProductId, N') WH ', w.Code, N': available ',
                      d.QuantityAvailable, N' → ', i.QuantityAvailable, N', reserved ',
                      d.QuantityReserved, N' → ', i.QuantityReserved, N', version ', i.Version),
               N'DB_TRIGGER'
        FROM inserted i
        JOIN deleted d             ON d.Id = i.Id
        LEFT JOIN dbo.Products p   ON p.Id = i.ProductId
        LEFT JOIN dbo.Warehouses w ON w.Id = i.WarehouseId
        WHERE i.QuantityAvailable <> d.QuantityAvailable
           OR i.QuantityReserved  <> d.QuantityReserved;
    END TRY
    BEGIN CATCH
        -- Auditing must never block a stock change; see header.
    END CATCH;
END;
GO

/* New rows in dbo.InventoryMovements — deliberately lean.
   Every movement is already a permanent, append-only ledger row (who, when, reference), the
   quantity effect is captured by trg_Inventory_Audit, and the business event (order confirmed,
   shipped, production registered) is audited by the application/procedures. Auditing every
   movement again would triple the audit volume with no new information. Only two cases are
   recorded:
   - 'Adjustment' movements: manual stock corrections are the classic fraud/error vector, so they
     get a dedicated, easy-to-filter audit entry (Action 'ManualAdjustment').
   - Unusually large physical movements (|Quantity| >= 1000 units of any type except the
     non-physical Reservation/ReservationRelease): Action 'LargeMovement'. */
CREATE OR ALTER TRIGGER dbo.trg_InventoryMovements_Audit
ON dbo.InventoryMovements
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;

    BEGIN TRY
        DECLARE @User nvarchar(50) = LEFT(COALESCE(CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
        DECLARE @Now datetime2 = SYSUTCDATETIME();
        DECLARE @LargeThreshold decimal(18,3) = 1000;

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        SELECT @Now, @User,
               CASE WHEN i.MovementType = N'Adjustment' THEN N'ManualAdjustment' ELSE N'LargeMovement' END,
               N'InventoryMovement', CAST(i.Id AS nvarchar(60)),
               LEFT(CONCAT(i.MovementType, N' ', i.Quantity, N' of ', p.ProductCode, N' in ', w.Code,
                           N', ref ', i.Reference, N', by ', i.UserName,
                           CASE WHEN i.Notes IS NOT NULL THEN N': ' + i.Notes END), 4000),
               N'DB_TRIGGER'
        FROM inserted i
        LEFT JOIN dbo.Products p   ON p.Id = i.ProductId
        LEFT JOIN dbo.Warehouses w ON w.Id = i.WarehouseId
        WHERE i.MovementType = N'Adjustment'
           OR (ABS(i.Quantity) >= @LargeThreshold AND i.MovementType NOT IN (N'Reservation', N'ReservationRelease'));
    END TRY
    BEGIN CATCH
        -- Auditing must never block a movement; see header.
    END CATCH;
END;
GO

/* Price and cost changes on dbo.Products. */
CREATE OR ALTER TRIGGER dbo.trg_Products_PriceAudit
ON dbo.Products
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF NOT (UPDATE(SellingPrice) OR UPDATE(Cost)) RETURN;

    BEGIN TRY
        DECLARE @User nvarchar(50) = LEFT(COALESCE(CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
        DECLARE @Now datetime2 = SYSUTCDATETIME();

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        SELECT @Now, @User, N'PriceChanged', N'Product', CAST(i.Id AS nvarchar(60)),
               CONCAT(i.ProductCode, N': price ', d.SellingPrice, N' → ', i.SellingPrice,
                      N', cost ', d.Cost, N' → ', i.Cost,
                      CASE WHEN d.SellingPrice > 0
                           THEN CONCAT(N' (', CAST(ROUND((i.SellingPrice - d.SellingPrice) * 100.0 / d.SellingPrice, 1) AS decimal(9,1)), N'% price)') END),
               N'DB_TRIGGER'
        FROM inserted i
        JOIN deleted d ON d.Id = i.Id
        WHERE i.SellingPrice <> d.SellingPrice
           OR i.Cost <> d.Cost;
    END TRY
    BEGIN CATCH
        -- Auditing must never block a catalog update; see header.
    END CATCH;
END;
GO

/* Status transitions of dbo.ProductionOrders (Planned -> InProgress -> Completed / Cancelled). */
CREATE OR ALTER TRIGGER dbo.trg_ProductionOrders_StatusAudit
ON dbo.ProductionOrders
AFTER UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT OFF;
    IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
    IF NOT UPDATE(Status) RETURN;

    BEGIN TRY
        DECLARE @User nvarchar(50) = LEFT(COALESCE(CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
        DECLARE @Now datetime2 = SYSUTCDATETIME();

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        SELECT @Now, @User, N'ProductionStatusChanged', N'ProductionOrder', CAST(i.Id AS nvarchar(60)),
               CONCAT(i.ProductionNumber, N': ', d.Status, N' → ', i.Status,
                      N' (completed ', i.QuantityCompleted, N'/', i.QuantityPlanned, N')'),
               N'DB_TRIGGER'
        FROM inserted i
        JOIN deleted d ON d.Id = i.Id
        WHERE i.Status <> d.Status;
    END TRY
    BEGIN CATCH
        -- Auditing must never block a production update; see header.
    END CATCH;
END;
GO
