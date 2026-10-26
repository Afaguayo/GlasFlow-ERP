/* =====================================================================================
   GlasFlow ERP — 03_stored_procedures.sql
   Transactional stored procedures that mirror the business rules of the .NET services
   (SalesOrderService, InventoryService, ProductionService, InventoryAlertService).

   Execution order:  01_schema.sql -> 02_seed_data.sql -> 03_stored_procedures.sql
                     -> 04_views.sql -> 05_triggers.sql -> 06_reports.sql (ad-hoc queries)

   - No USE statement: run it against the target database, e.g.
       sqlcmd -S .\SQLEXPRESS -E -C -b -d GlasFlowERP -i database\03_stored_procedures.sql
     The API (SqlServerObjectsInstaller) also applies this file on every start-up.
   - CREATE OR ALTER: re-running is safe (SQL Server 2016 SP1+).

   Conventions shared by every procedure
   - SET NOCOUNT ON / SET XACT_ABORT ON, BEGIN TRY + BEGIN TRAN ... COMMIT, CATCH -> ROLLBACK + THROW.
   - Rows that are read and then written are read WITH (UPDLOCK, HOLDLOCK) so two sessions cannot
     both pass a stock check and then both write (no lost updates, no phantom order numbers).
   - Status strings are exactly the values allowed by the CHECK constraints:
       SalesOrders.Status      'Draft','Confirmed','Shipped','Cancelled'
       ProductionOrders.Status 'Planned','InProgress','Completed','Cancelled'
       InventoryMovements.MovementType 'Adjustment','Reservation','ReservationRelease','SaleShipment',
                                       'ProductionReceipt','TransferOut','TransferIn','Import'
   - Free stock = QuantityAvailable - QuantityReserved; no procedure leaves free stock below zero.
   - Every stock change increments Inventory.Version (the EF concurrency token) and sets UpdatedAt,
     so an EF client holding a stale row gets a DbUpdateConcurrencyException instead of overwriting.
   - Business audit rows go to dbo.AuditLogs with Source = 'DB_PROC' (the API writes 'APP',
     the triggers in 05_triggers.sql write 'DB_TRIGGER').
   - The acting user (@UserName, else SESSION_CONTEXT('UserName'), else SUSER_SNAME()) is stored in
     SESSION_CONTEXT(N'UserName') so the audit triggers attribute their rows to the same person.
   - All timestamps are UTC (SYSUTCDATETIME()), like the application.
   - Errors: 50001 = not found, 50002 = business rule violated, 50003 = duplicate / conflict.
   ===================================================================================== */

/* -------------------------------------------------------------------------------------
   usp_RunLowStockCheck
   Same semantics as InventoryAlertService: for each in-scope stock row of an active product,
   ensure exactly one open LowStock notification (DedupKey 'LOWSTOCK:{productId}:{warehouseId}')
   while free <= MinimumStock (MinimumStock > 0), and resolve the open one once stock recovers.
   Idempotent: running it twice in a row creates nothing the second time. The filtered unique index
   IX_Notifications_DedupKey (DedupKey IS NOT NULL AND IsResolved = 0) is the last line of defence.
   Both parameters NULL = full check (also writes an 'InventoryCheck' audit row, like RunFullCheckAsync).
   Defined first because the other procedures call it after they commit.
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_RunLowStockCheck
    @ProductId    int = NULL,
    @WarehouseId  int = NULL,
    @UserName     nvarchar(50) = NULL,
    @ReturnResult bit = 1,
    @Created      int = NULL OUTPUT,
    @Resolved     int = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Checked int, @Low int;

    DECLARE @Scope TABLE
    (
        ProductId   int           NOT NULL,
        WarehouseId int           NOT NULL,
        DedupKey    nvarchar(80)  NOT NULL PRIMARY KEY,
        IsLow       bit           NOT NULL,
        QtyFree     decimal(18,3) NOT NULL,
        MinimumStock decimal(18,3) NOT NULL,
        ProductCode nvarchar(30)  NOT NULL,
        ProductName nvarchar(150) NOT NULL,
        Unit        nvarchar(10)  NOT NULL,
        WarehouseCode nvarchar(20) NOT NULL
    );

    BEGIN TRY
        BEGIN TRANSACTION;

        INSERT @Scope (ProductId, WarehouseId, DedupKey, IsLow, QtyFree, MinimumStock, ProductCode, ProductName, Unit, WarehouseCode)
        SELECT i.ProductId, i.WarehouseId,
               CONCAT(N'LOWSTOCK:', i.ProductId, N':', i.WarehouseId),
               CASE WHEN p.MinimumStock > 0 AND i.QuantityAvailable - i.QuantityReserved <= p.MinimumStock THEN 1 ELSE 0 END,
               i.QuantityAvailable - i.QuantityReserved, p.MinimumStock, p.ProductCode, p.ProductName, p.Unit, w.Code
        FROM dbo.Inventory i
        JOIN dbo.Products p   ON p.Id = i.ProductId
        JOIN dbo.Warehouses w ON w.Id = i.WarehouseId
        WHERE p.Active = 1
          AND (@ProductId IS NULL OR i.ProductId = @ProductId)
          AND (@WarehouseId IS NULL OR i.WarehouseId = @WarehouseId);

        SELECT @Checked = COUNT(*), @Low = ISNULL(SUM(CAST(IsLow AS int)), 0) FROM @Scope;

        -- New alerts: low rows that have no open notification for their key yet.
        INSERT dbo.Notifications (Type, Severity, Title, Message, ProductId, WarehouseId, DedupKey, IsRead, IsResolved, CreatedAt)
        SELECT N'LowStock',
               CASE WHEN s.QtyFree <= 0 THEN N'Critical' ELSE N'Warning' END,
               LEFT(N'Inventario bajo: ' + s.ProductCode, 150),
               LEFT(s.ProductName + N' en ' + s.WarehouseCode + N': ' + FORMAT(s.QtyFree, N'0.##', 'en-US') + N' ' + s.Unit
                    + N' libres (mínimo ' + FORMAT(s.MinimumStock, N'0.##', 'en-US')
                    + N'). Considere una orden de producción o compra.', 500),
               s.ProductId, s.WarehouseId, s.DedupKey, 0, 0, @Now
        FROM @Scope s
        WHERE s.IsLow = 1
          AND NOT EXISTS (SELECT 1 FROM dbo.Notifications n WITH (UPDLOCK, HOLDLOCK)
                          WHERE n.DedupKey = s.DedupKey AND n.IsResolved = 0);
        SET @Created = @@ROWCOUNT;

        -- Recovered rows: resolve their open LowStock notification.
        UPDATE n
           SET IsResolved = 1, ResolvedAt = @Now
        FROM dbo.Notifications n WITH (UPDLOCK, HOLDLOCK)
        JOIN @Scope s ON s.DedupKey = n.DedupKey
        WHERE n.Type = N'LowStock' AND n.IsResolved = 0 AND s.IsLow = 0;
        SET @Resolved = @@ROWCOUNT;

        IF @ProductId IS NULL AND @WarehouseId IS NULL
            INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
            VALUES (@Now, @User, N'InventoryCheck', N'Inventory', NULL,
                    CONCAT(N'checked ', @Checked, N', low ', @Low, N', created ', @Created, N', resolved ', @Resolved), N'DB_PROC');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    IF @ReturnResult = 1
        SELECT @Now AS CheckedAt, @Checked AS RowsChecked, @Low AS LowStockRows, @Created AS AlertsCreated, @Resolved AS AlertsResolved;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_CreateSalesOrder
   Creates a Draft order from a JSON array of lines:
     [{"productId":1,"quantity":10,"unitPrice":null}, ...]   (unitPrice null/omitted = list price)
   Rules (SalesOrderService.CreateAsync + SalesOrder.AddLine/RecalculateTotals):
   at least one line; a product only once per order; ExternalReference unique (idempotent client
   submissions); customer, warehouse and products exist and are active; quantity > 0; price > 0;
   LineTotal = ROUND(qty * price, 2); Subtotal = SUM(LineTotal); Tax = ROUND(Subtotal * rate, 2) with
   rate 0.16 (General) or 0.08 (FronteraNorte); Total = Subtotal + Tax.
   OrderNumber 'SO-YYYY-NNNN' continues the yearly sequence (range-locked, so concurrent calls
   cannot pick the same number).
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_CreateSalesOrder
    @CustomerId        int,
    @WarehouseId       int,
    @Lines             nvarchar(max),
    @Notes             nvarchar(500) = NULL,
    @ExternalReference nvarchar(60)  = NULL,
    @UserName          nvarchar(50)  = NULL,
    @SalesOrderId      int = NULL OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @BadId int, @BadCode nvarchar(30);
    SET @ExternalReference = NULLIF(LTRIM(RTRIM(@ExternalReference)), N'');
    SET @SalesOrderId = NULL;

    IF @Lines IS NULL OR ISJSON(@Lines) = 0 OR LEFT(LTRIM(@Lines), 1) <> N'['
        THROW 50002, N'@Lines must be a JSON array such as [{"productId":1,"quantity":10,"unitPrice":null}].', 1;

    DECLARE @L TABLE (ProductId int NULL, Quantity decimal(18,3) NULL, UnitPrice decimal(18,2) NULL);
    INSERT @L (ProductId, Quantity, UnitPrice)
    SELECT productId, quantity, unitPrice
    FROM OPENJSON(@Lines) WITH (productId int '$.productId', quantity decimal(18,3) '$.quantity', unitPrice decimal(18,2) '$.unitPrice');

    IF NOT EXISTS (SELECT 1 FROM @L)
        THROW 50002, N'An order needs at least one line.', 1;
    IF EXISTS (SELECT 1 FROM @L WHERE ProductId IS NULL)
        THROW 50002, N'Every line needs a productId.', 1;
    IF EXISTS (SELECT 1 FROM @L GROUP BY ProductId HAVING COUNT(*) > 1)
        THROW 50002, N'Each product may appear only once per order; combine the quantities.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF @ExternalReference IS NOT NULL AND EXISTS
           (SELECT 1 FROM dbo.SalesOrders WITH (UPDLOCK, HOLDLOCK) WHERE ExternalReference = @ExternalReference)
        BEGIN
            SET @Msg = CONCAT(N'An order with reference ''', @ExternalReference, N''' already exists (duplicate submission).');
            THROW 50003, @Msg, 1;
        END;

        DECLARE @TaxZone nvarchar(20), @CustActive bit, @CustCode nvarchar(20);
        SELECT @TaxZone = TaxZone, @CustActive = Active, @CustCode = CustomerCode FROM dbo.Customers WHERE Id = @CustomerId;
        IF @TaxZone IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Customer ''', @CustomerId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF @CustActive = 0
        BEGIN
            SET @Msg = CONCAT(N'Customer ', @CustCode, N' is inactive.');
            THROW 50002, @Msg, 1;
        END;

        DECLARE @WhActive bit, @WhCode nvarchar(20);
        SELECT @WhActive = Active, @WhCode = Code FROM dbo.Warehouses WHERE Id = @WarehouseId;
        IF @WhCode IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Warehouse ''', @WarehouseId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF @WhActive = 0
        BEGIN
            SET @Msg = CONCAT(N'Warehouse ', @WhCode, N' is inactive.');
            THROW 50002, @Msg, 1;
        END;

        SELECT TOP (1) @BadId = l.ProductId FROM @L l LEFT JOIN dbo.Products p ON p.Id = l.ProductId WHERE p.Id IS NULL;
        IF @BadId IS NOT NULL
        BEGIN
            SET @Msg = CONCAT(N'Product ''', @BadId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;

        SELECT TOP (1) @BadCode = p.ProductCode FROM @L l JOIN dbo.Products p ON p.Id = l.ProductId WHERE p.Active = 0;
        IF @BadCode IS NOT NULL
        BEGIN
            SET @Msg = CONCAT(N'Product ', @BadCode, N' is inactive.');
            THROW 50002, @Msg, 1;
        END;
        IF EXISTS (SELECT 1 FROM @L WHERE Quantity IS NULL OR Quantity <= 0)
            THROW 50002, N'Line quantity must be greater than zero.', 1;
        IF EXISTS (SELECT 1 FROM @L l JOIN dbo.Products p ON p.Id = l.ProductId WHERE COALESCE(l.UnitPrice, p.SellingPrice) <= 0)
            THROW 50002, N'Unit price must be greater than zero.', 1;

        -- Next number of the year. UPDLOCK + HOLDLOCK range-locks the prefix so two sessions serialize here.
        DECLARE @Prefix nvarchar(8) = CONCAT(N'SO-', YEAR(@Now), N'-');
        DECLARE @Last nvarchar(20), @Next int, @OrderNumber nvarchar(20);
        SELECT TOP (1) @Last = OrderNumber
        FROM dbo.SalesOrders WITH (UPDLOCK, HOLDLOCK)
        WHERE OrderNumber LIKE @Prefix + N'%'
        ORDER BY OrderNumber DESC;
        SET @Next = ISNULL(TRY_CAST(SUBSTRING(@Last, LEN(@Prefix) + 1, 10) AS int), 0) + 1;
        SET @OrderNumber = @Prefix + CASE WHEN @Next > 9999 THEN CAST(@Next AS nvarchar(10))
                                          ELSE RIGHT(N'0000' + CAST(@Next AS nvarchar(10)), 4) END;

        DECLARE @TaxRate decimal(5,4) = CASE WHEN @TaxZone = N'FronteraNorte' THEN 0.08 ELSE 0.16 END;
        DECLARE @Priced TABLE (ProductId int PRIMARY KEY, Quantity decimal(18,3), UnitPrice decimal(18,2), LineTotal decimal(18,2));
        INSERT @Priced (ProductId, Quantity, UnitPrice, LineTotal)
        SELECT l.ProductId, l.Quantity, COALESCE(l.UnitPrice, p.SellingPrice),
               ROUND(l.Quantity * COALESCE(l.UnitPrice, p.SellingPrice), 2)
        FROM @L l JOIN dbo.Products p ON p.Id = l.ProductId;

        DECLARE @Subtotal decimal(18,2) = (SELECT ROUND(SUM(LineTotal), 2) FROM @Priced);
        DECLARE @Tax decimal(18,2) = ROUND(@Subtotal * @TaxRate, 2);
        DECLARE @Total decimal(18,2) = @Subtotal + @Tax;

        INSERT dbo.SalesOrders (OrderNumber, CustomerId, WarehouseId, OrderDate, Status, TaxRate, Subtotal, Tax, Total,
                                Notes, ExternalReference, ConfirmedAt, ShippedAt, CreatedAt, UpdatedAt)
        VALUES (@OrderNumber, @CustomerId, @WarehouseId, @Now, N'Draft', @TaxRate, @Subtotal, @Tax, @Total,
                @Notes, @ExternalReference, NULL, NULL, @Now, @Now);
        SET @SalesOrderId = CAST(SCOPE_IDENTITY() AS int);

        INSERT dbo.SalesOrderDetails (SalesOrderId, ProductId, Quantity, UnitPrice, LineTotal)
        SELECT @SalesOrderId, ProductId, Quantity, UnitPrice, LineTotal FROM @Priced;

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'SalesOrderCreated', N'SalesOrder', CAST(@SalesOrderId AS nvarchar(60)),
                CONCAT(@OrderNumber, N' total ', @Total, N' MXN'), N'DB_PROC');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        SET @SalesOrderId = NULL;
        THROW;
    END CATCH;

    SELECT Id AS SalesOrderId, OrderNumber, Status, TaxRate, Subtotal, Tax, Total FROM dbo.SalesOrders WHERE Id = @SalesOrderId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_ConfirmSalesOrder
   Draft -> Confirmed. Reserves the stock of every line in the order's warehouse, all or nothing:
   if any line lacks free stock the whole call fails and the message lists every shortage.
   Writes one 'Reservation' movement per line (positive quantity; it does not change on-hand stock).
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_ConfirmSalesOrder
    @SalesOrderId int,
    @UserName     nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @Status nvarchar(20), @OrderNumber nvarchar(20), @WarehouseId int,
            @CustomerName nvarchar(150), @Total decimal(18,2), @Shortages nvarchar(max);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Status = o.Status, @OrderNumber = o.OrderNumber, @WarehouseId = o.WarehouseId, @Total = o.Total, @CustomerName = c.Name
        FROM dbo.SalesOrders o WITH (UPDLOCK, HOLDLOCK)
        JOIN dbo.Customers c ON c.Id = o.CustomerId
        WHERE o.Id = @SalesOrderId;

        IF @Status IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Sales order ''', @SalesOrderId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF @Status <> N'Draft'
        BEGIN
            SET @Msg = CONCAT(N'Cannot confirm order ', @OrderNumber, N' in status ', @Status, N'; it must be Draft.');
            THROW 50002, @Msg, 1;
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.SalesOrderDetails WHERE SalesOrderId = @SalesOrderId)
            THROW 50002, N'Cannot confirm an order without lines.', 1;

        -- Get-or-create the stock rows (InventoryService.GetOrCreateStockAsync).
        INSERT dbo.Inventory (ProductId, WarehouseId, QuantityAvailable, QuantityReserved, UpdatedAt, Version)
        SELECT d.ProductId, @WarehouseId, 0, 0, @Now, 0
        FROM dbo.SalesOrderDetails d
        WHERE d.SalesOrderId = @SalesOrderId
          AND NOT EXISTS (SELECT 1 FROM dbo.Inventory i WITH (UPDLOCK, HOLDLOCK)
                          WHERE i.ProductId = d.ProductId AND i.WarehouseId = @WarehouseId);

        -- Lock the stock rows, then check every line before touching any of them.
        SELECT @Shortages = STUFF((
            SELECT N'; ' + p.ProductCode + N' (requested ' + CONVERT(nvarchar(30), d.Quantity)
                   + N', free ' + CONVERT(nvarchar(30), i.QuantityAvailable - i.QuantityReserved) + N')'
            FROM dbo.SalesOrderDetails d
            JOIN dbo.Inventory i WITH (UPDLOCK, HOLDLOCK) ON i.ProductId = d.ProductId AND i.WarehouseId = @WarehouseId
            JOIN dbo.Products p ON p.Id = d.ProductId
            WHERE d.SalesOrderId = @SalesOrderId
              AND i.QuantityAvailable - i.QuantityReserved < d.Quantity
            ORDER BY d.Id
            FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 2, N'');

        IF @Shortages IS NOT NULL
        BEGIN
            SET @Msg = LEFT(N'Insufficient stock in warehouse for: ' + @Shortages + N'.', 2048);
            THROW 50002, @Msg, 1;
        END;

        UPDATE i
           SET QuantityReserved = i.QuantityReserved + d.Quantity,
               Version = i.Version + 1,
               UpdatedAt = @Now
        FROM dbo.Inventory i
        JOIN dbo.SalesOrderDetails d ON d.ProductId = i.ProductId AND d.SalesOrderId = @SalesOrderId
        WHERE i.WarehouseId = @WarehouseId;

        INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
        SELECT d.ProductId, @WarehouseId, N'Reservation', d.Quantity, @OrderNumber, N'Reserved for order', @Now, @User
        FROM dbo.SalesOrderDetails d
        WHERE d.SalesOrderId = @SalesOrderId;

        UPDATE dbo.SalesOrders
           SET Status = N'Confirmed', ConfirmedAt = @Now, UpdatedAt = @Now
        WHERE Id = @SalesOrderId;

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'SalesOrderConfirmed', N'SalesOrder', CAST(@SalesOrderId AS nvarchar(60)), @OrderNumber, N'DB_PROC');

        INSERT dbo.Notifications (Type, Severity, Title, Message, ProductId, WarehouseId, DedupKey, IsRead, IsResolved, CreatedAt)
        VALUES (N'OrderConfirmed', N'Info', LEFT(N'Pedido ' + @OrderNumber + N' confirmado', 150),
                LEFT(@CustomerName + N': ' + FORMAT(@Total, N'N2', 'en-US') + N' MXN, inventario reservado.', 500),
                NULL, NULL, NULL, 0, 0, @Now);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    -- Like the service: re-evaluate low-stock alerts after the commit.
    EXEC dbo.usp_RunLowStockCheck @WarehouseId = @WarehouseId, @UserName = @User, @ReturnResult = 0;

    SELECT Id AS SalesOrderId, OrderNumber, Status, ConfirmedAt, Total FROM dbo.SalesOrders WHERE Id = @SalesOrderId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_ShipSalesOrder
   Confirmed -> Shipped. The reserved stock physically leaves the warehouse: QuantityReserved and
   QuantityAvailable both drop by the line quantity, one 'SaleShipment' movement per line with a
   NEGATIVE quantity (InventoryItem.ShipReserved).
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_ShipSalesOrder
    @SalesOrderId int,
    @UserName     nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @Status nvarchar(20), @OrderNumber nvarchar(20), @WarehouseId int,
            @CustomerName nvarchar(150), @LineCount int, @BadCode nvarchar(30);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Status = o.Status, @OrderNumber = o.OrderNumber, @WarehouseId = o.WarehouseId, @CustomerName = c.Name
        FROM dbo.SalesOrders o WITH (UPDLOCK, HOLDLOCK)
        JOIN dbo.Customers c ON c.Id = o.CustomerId
        WHERE o.Id = @SalesOrderId;

        IF @Status IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Sales order ''', @SalesOrderId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF @Status <> N'Confirmed'
        BEGIN
            SET @Msg = CONCAT(N'Cannot ship order ', @OrderNumber, N' in status ', @Status, N'; it must be Confirmed.');
            THROW 50002, @Msg, 1;
        END;

        -- Every line must still be covered by its reservation (a missing stock row counts as 0 reserved).
        SELECT TOP (1) @BadCode = p.ProductCode
        FROM dbo.SalesOrderDetails d
        JOIN dbo.Products p ON p.Id = d.ProductId
        LEFT JOIN dbo.Inventory i WITH (UPDLOCK, HOLDLOCK) ON i.ProductId = d.ProductId AND i.WarehouseId = @WarehouseId
        WHERE d.SalesOrderId = @SalesOrderId
          AND (i.Id IS NULL OR i.QuantityReserved < d.Quantity OR i.QuantityAvailable < d.Quantity)
        ORDER BY d.Id;
        IF @BadCode IS NOT NULL
        BEGIN
            SET @Msg = CONCAT(N'Cannot ship more than the reserved quantity (product ', @BadCode, N').');
            THROW 50002, @Msg, 1;
        END;

        UPDATE i
           SET QuantityReserved  = i.QuantityReserved - d.Quantity,
               QuantityAvailable = i.QuantityAvailable - d.Quantity,
               Version = i.Version + 1,
               UpdatedAt = @Now
        FROM dbo.Inventory i
        JOIN dbo.SalesOrderDetails d ON d.ProductId = i.ProductId AND d.SalesOrderId = @SalesOrderId
        WHERE i.WarehouseId = @WarehouseId;
        SET @LineCount = @@ROWCOUNT;

        INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
        SELECT d.ProductId, @WarehouseId, N'SaleShipment', -d.Quantity, @OrderNumber, NULL, @Now, @User
        FROM dbo.SalesOrderDetails d
        WHERE d.SalesOrderId = @SalesOrderId;

        UPDATE dbo.SalesOrders
           SET Status = N'Shipped', ShippedAt = @Now, UpdatedAt = @Now
        WHERE Id = @SalesOrderId;

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'SalesOrderShipped', N'SalesOrder', CAST(@SalesOrderId AS nvarchar(60)), @OrderNumber, N'DB_PROC');

        INSERT dbo.Notifications (Type, Severity, Title, Message, ProductId, WarehouseId, DedupKey, IsRead, IsResolved, CreatedAt)
        VALUES (N'OrderShipped', N'Info', LEFT(N'Pedido ' + @OrderNumber + N' embarcado', 150),
                LEFT(CONCAT(@LineCount, N' partidas enviadas a ', @CustomerName, N'.'), 500), NULL, NULL, NULL, 0, 0, @Now);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC dbo.usp_RunLowStockCheck @WarehouseId = @WarehouseId, @UserName = @User, @ReturnResult = 0;

    SELECT Id AS SalesOrderId, OrderNumber, Status, ShippedAt, Total FROM dbo.SalesOrders WHERE Id = @SalesOrderId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_CancelSalesOrder
   Draft/Confirmed -> Cancelled (Shipped or Cancelled orders cannot be cancelled). A confirmed
   order releases its reservation: one 'ReservationRelease' movement per line (negative quantity).
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_CancelSalesOrder
    @SalesOrderId int,
    @UserName     nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @Status nvarchar(20), @OrderNumber nvarchar(20), @WarehouseId int;

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Status = Status, @OrderNumber = OrderNumber, @WarehouseId = WarehouseId
        FROM dbo.SalesOrders WITH (UPDLOCK, HOLDLOCK)
        WHERE Id = @SalesOrderId;

        IF @Status IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Sales order ''', @SalesOrderId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF @Status IN (N'Shipped', N'Cancelled')
        BEGIN
            SET @Msg = CONCAT(N'Cannot cancel an order in status ', @Status, N'.');
            THROW 50002, @Msg, 1;
        END;

        IF @Status = N'Confirmed'
        BEGIN
            UPDATE i
               SET QuantityReserved = CASE WHEN i.QuantityReserved - d.Quantity < 0 THEN 0 ELSE i.QuantityReserved - d.Quantity END,
                   Version = i.Version + 1,
                   UpdatedAt = @Now
            FROM dbo.Inventory i WITH (UPDLOCK, HOLDLOCK)
            JOIN dbo.SalesOrderDetails d ON d.ProductId = i.ProductId AND d.SalesOrderId = @SalesOrderId
            WHERE i.WarehouseId = @WarehouseId;

            INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
            SELECT d.ProductId, @WarehouseId, N'ReservationRelease', -d.Quantity, @OrderNumber, N'Order cancelled', @Now, @User
            FROM dbo.SalesOrderDetails d
            WHERE d.SalesOrderId = @SalesOrderId;
        END;

        UPDATE dbo.SalesOrders SET Status = N'Cancelled', UpdatedAt = @Now WHERE Id = @SalesOrderId;

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'SalesOrderCancelled', N'SalesOrder', CAST(@SalesOrderId AS nvarchar(60)), @OrderNumber, N'DB_PROC');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    IF @Status = N'Confirmed'
        EXEC dbo.usp_RunLowStockCheck @WarehouseId = @WarehouseId, @UserName = @User, @ReturnResult = 0;

    SELECT Id AS SalesOrderId, OrderNumber, Status FROM dbo.SalesOrders WHERE Id = @SalesOrderId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_UpdateInventory
   Signed manual adjustment (cycle count, breakage...). Creates the stock row when it is missing.
   Refuses a zero delta, a result below zero and a result below the reserved quantity
   (InventoryItem.Adjust with AllowNegativeStock = false). Writes an 'Adjustment' movement with
   reference 'ADJ-yyyyMMddHHmmss' and the reason in Notes.
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_UpdateInventory
    @ProductId   int,
    @WarehouseId int,
    @Delta       decimal(18,3),
    @Reason      nvarchar(250) = NULL,
    @UserName    nvarchar(50)  = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @InventoryId int, @Available decimal(18,3), @Reserved decimal(18,3), @Result decimal(18,3);

    IF @Delta IS NULL OR @Delta = 0
        THROW 50002, N'Adjustment quantity cannot be zero.', 1;

    BEGIN TRY
        BEGIN TRANSACTION;

        IF NOT EXISTS (SELECT 1 FROM dbo.Products WHERE Id = @ProductId)
        BEGIN
            SET @Msg = CONCAT(N'Product ''', @ProductId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF NOT EXISTS (SELECT 1 FROM dbo.Warehouses WHERE Id = @WarehouseId)
        BEGIN
            SET @Msg = CONCAT(N'Warehouse ''', @WarehouseId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;

        SELECT @InventoryId = Id, @Available = QuantityAvailable, @Reserved = QuantityReserved
        FROM dbo.Inventory WITH (UPDLOCK, HOLDLOCK)
        WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId;

        IF @InventoryId IS NULL
        BEGIN
            INSERT dbo.Inventory (ProductId, WarehouseId, QuantityAvailable, QuantityReserved, UpdatedAt, Version)
            VALUES (@ProductId, @WarehouseId, 0, 0, @Now, 0);
            SELECT @InventoryId = CAST(SCOPE_IDENTITY() AS int), @Available = 0, @Reserved = 0;
        END;

        SET @Result = @Available + @Delta;
        IF @Result < 0
        BEGIN
            SET @Msg = CONCAT(N'Adjustment would leave negative stock (', @Result, N').');
            THROW 50002, @Msg, 1;
        END;
        IF @Result < @Reserved
        BEGIN
            SET @Msg = CONCAT(N'Adjustment would leave less stock (', @Result, N') than is reserved (', @Reserved, N').');
            THROW 50002, @Msg, 1;
        END;

        UPDATE dbo.Inventory
           SET QuantityAvailable = @Result, Version = Version + 1, UpdatedAt = @Now
        WHERE Id = @InventoryId;

        INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
        VALUES (@ProductId, @WarehouseId, N'Adjustment', @Delta, N'ADJ-' + FORMAT(@Now, N'yyyyMMddHHmmss', 'en-US'), @Reason, @Now, @User);

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'StockAdjusted', N'Inventory', CAST(@InventoryId AS nvarchar(60)),
                CONCAT(N'Product ', @ProductId, N' WH ', @WarehouseId, N' delta ', @Delta, N': ', @Reason), N'DB_PROC');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC dbo.usp_RunLowStockCheck @ProductId = @ProductId, @WarehouseId = @WarehouseId, @UserName = @User, @ReturnResult = 0;

    SELECT Id AS InventoryId, ProductId, WarehouseId, QuantityAvailable, QuantityReserved,
           QuantityAvailable - QuantityReserved AS QuantityFree, Version, UpdatedAt
    FROM dbo.Inventory WHERE Id = @InventoryId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_StartProductionOrder
   Planned -> InProgress, sets StartDate.
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_StartProductionOrder
    @ProductionOrderId int,
    @UserName          nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @Status nvarchar(20), @Number nvarchar(20);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Status = Status, @Number = ProductionNumber
        FROM dbo.ProductionOrders WITH (UPDLOCK, HOLDLOCK)
        WHERE Id = @ProductionOrderId;

        IF @Status IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Production order ''', @ProductionOrderId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF @Status <> N'Planned'
        BEGIN
            SET @Msg = CONCAT(N'Only planned orders can be started (current: ', @Status, N').');
            THROW 50002, @Msg, 1;
        END;

        UPDATE dbo.ProductionOrders
           SET Status = N'InProgress', StartDate = @Now, UpdatedAt = @Now
        WHERE Id = @ProductionOrderId;

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'ProductionStarted', N'ProductionOrder', CAST(@ProductionOrderId AS nvarchar(60)), @Number, N'DB_PROC');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    SELECT Id AS ProductionOrderId, ProductionNumber, Status, StartDate FROM dbo.ProductionOrders WHERE Id = @ProductionOrderId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_RegisterProduction
   Registers finished units of an InProgress order (ProductionOrder.RegisterCompleted):
   quantity > 0 and QuantityCompleted + quantity <= QuantityPlanned. The units are received into
   the destination warehouse at once (stock row created if missing) with a 'ProductionReceipt'
   movement referencing the production number.
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_RegisterProduction
    @ProductionOrderId int,
    @Quantity          decimal(18,3),
    @UserName          nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @Status nvarchar(20), @Number nvarchar(20), @ProductId int, @WarehouseId int,
            @Planned decimal(18,3), @Completed decimal(18,3);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Status = Status, @Number = ProductionNumber, @ProductId = ProductId, @WarehouseId = WarehouseId,
               @Planned = QuantityPlanned, @Completed = QuantityCompleted
        FROM dbo.ProductionOrders WITH (UPDLOCK, HOLDLOCK)
        WHERE Id = @ProductionOrderId;

        IF @Status IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Production order ''', @ProductionOrderId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;
        IF @Status <> N'InProgress'
        BEGIN
            SET @Msg = CONCAT(N'Production can only be registered while in progress (current: ', @Status, N').');
            THROW 50002, @Msg, 1;
        END;
        IF @Quantity IS NULL OR @Quantity <= 0
            THROW 50002, N'Completed quantity must be greater than zero.', 1;
        IF @Completed + @Quantity > @Planned
        BEGIN
            SET @Msg = CONCAT(N'Completed quantity would exceed the planned ', @Planned, N'.');
            THROW 50002, @Msg, 1;
        END;

        UPDATE dbo.ProductionOrders
           SET QuantityCompleted = QuantityCompleted + @Quantity, UpdatedAt = @Now
        WHERE Id = @ProductionOrderId;

        IF NOT EXISTS (SELECT 1 FROM dbo.Inventory WITH (UPDLOCK, HOLDLOCK) WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId)
            INSERT dbo.Inventory (ProductId, WarehouseId, QuantityAvailable, QuantityReserved, UpdatedAt, Version)
            VALUES (@ProductId, @WarehouseId, 0, 0, @Now, 0);

        UPDATE dbo.Inventory
           SET QuantityAvailable = QuantityAvailable + @Quantity, Version = Version + 1, UpdatedAt = @Now
        WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId;

        INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
        VALUES (@ProductId, @WarehouseId, N'ProductionReceipt', @Quantity, @Number, NULL, @Now, @User);

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'ProductionRegistered', N'ProductionOrder', CAST(@ProductionOrderId AS nvarchar(60)),
                CONCAT(@Number, N' +', @Quantity), N'DB_PROC');

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC dbo.usp_RunLowStockCheck @ProductId = @ProductId, @WarehouseId = @WarehouseId, @UserName = @User, @ReturnResult = 0;

    SELECT Id AS ProductionOrderId, ProductionNumber, Status, QuantityPlanned, QuantityCompleted
    FROM dbo.ProductionOrders WHERE Id = @ProductionOrderId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_CompleteProductionOrder
   InProgress -> Completed. When @FinalQuantity > 0 it is registered first (same rules and stock
   receipt as usp_RegisterProduction) in the same transaction. Completing requires at least one
   registered unit (QuantityCompleted > 0). Sets CompletionDate and raises a ProductionCompleted
   notification, like ProductionService.CompleteAsync.
   (The registration logic is repeated here instead of calling usp_RegisterProduction because
   nesting two procedures that each own BEGIN/COMMIT TRAN complicates error handling.)
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_CompleteProductionOrder
    @ProductionOrderId int,
    @FinalQuantity     decimal(18,3) = NULL,
    @UserName          nvarchar(50)  = NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @User nvarchar(50) = LEFT(COALESCE(NULLIF(LTRIM(RTRIM(@UserName)), N''),
                                               CAST(SESSION_CONTEXT(N'UserName') AS nvarchar(50)), SUSER_SNAME()), 50);
    EXEC sys.sp_set_session_context @key = N'UserName', @value = @User;
    DECLARE @Now datetime2 = SYSUTCDATETIME();
    DECLARE @Msg nvarchar(2048), @Status nvarchar(20), @Number nvarchar(20), @ProductId int, @WarehouseId int,
            @Planned decimal(18,3), @Completed decimal(18,3), @ProductName nvarchar(150), @Unit nvarchar(10), @WhCode nvarchar(20);

    BEGIN TRY
        BEGIN TRANSACTION;

        SELECT @Status = po.Status, @Number = po.ProductionNumber, @ProductId = po.ProductId, @WarehouseId = po.WarehouseId,
               @Planned = po.QuantityPlanned, @Completed = po.QuantityCompleted,
               @ProductName = p.ProductName, @Unit = p.Unit, @WhCode = w.Code
        FROM dbo.ProductionOrders po WITH (UPDLOCK, HOLDLOCK)
        JOIN dbo.Products p   ON p.Id = po.ProductId
        JOIN dbo.Warehouses w ON w.Id = po.WarehouseId
        WHERE po.Id = @ProductionOrderId;

        IF @Status IS NULL
        BEGIN
            SET @Msg = CONCAT(N'Production order ''', @ProductionOrderId, N''' was not found.');
            THROW 50001, @Msg, 1;
        END;

        IF @FinalQuantity > 0
        BEGIN
            IF @Status <> N'InProgress'
            BEGIN
                SET @Msg = CONCAT(N'Production can only be registered while in progress (current: ', @Status, N').');
                THROW 50002, @Msg, 1;
            END;
            IF @Completed + @FinalQuantity > @Planned
            BEGIN
                SET @Msg = CONCAT(N'Completed quantity would exceed the planned ', @Planned, N'.');
                THROW 50002, @Msg, 1;
            END;

            SET @Completed = @Completed + @FinalQuantity;
            UPDATE dbo.ProductionOrders SET QuantityCompleted = @Completed, UpdatedAt = @Now WHERE Id = @ProductionOrderId;

            IF NOT EXISTS (SELECT 1 FROM dbo.Inventory WITH (UPDLOCK, HOLDLOCK) WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId)
                INSERT dbo.Inventory (ProductId, WarehouseId, QuantityAvailable, QuantityReserved, UpdatedAt, Version)
                VALUES (@ProductId, @WarehouseId, 0, 0, @Now, 0);

            UPDATE dbo.Inventory
               SET QuantityAvailable = QuantityAvailable + @FinalQuantity, Version = Version + 1, UpdatedAt = @Now
            WHERE ProductId = @ProductId AND WarehouseId = @WarehouseId;

            INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
            VALUES (@ProductId, @WarehouseId, N'ProductionReceipt', @FinalQuantity, @Number, NULL, @Now, @User);
        END;

        IF @Status <> N'InProgress'
        BEGIN
            SET @Msg = CONCAT(N'Only in-progress orders can be completed (current: ', @Status, N').');
            THROW 50002, @Msg, 1;
        END;
        IF @Completed <= 0
            THROW 50002, N'Register at least one completed unit before completing the order.', 1;

        UPDATE dbo.ProductionOrders
           SET Status = N'Completed', CompletionDate = @Now, UpdatedAt = @Now
        WHERE Id = @ProductionOrderId;

        INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
        VALUES (@Now, @User, N'ProductionCompleted', N'ProductionOrder', CAST(@ProductionOrderId AS nvarchar(60)),
                CONCAT(@Number, N' completed ', @Completed, N'/', @Planned), N'DB_PROC');

        INSERT dbo.Notifications (Type, Severity, Title, Message, ProductId, WarehouseId, DedupKey, IsRead, IsResolved, CreatedAt)
        VALUES (N'ProductionCompleted', N'Info', LEFT(N'Producción ' + @Number + N' terminada', 150),
                LEFT(CONCAT(@ProductName, N': ', @Completed, N' ', @Unit, N' ingresados a ', @WhCode, N'.'), 500),
                @ProductId, @WarehouseId, NULL, 0, 0, @Now);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH;

    EXEC dbo.usp_RunLowStockCheck @ProductId = @ProductId, @WarehouseId = @WarehouseId, @UserName = @User, @ReturnResult = 0;

    SELECT Id AS ProductionOrderId, ProductionNumber, Status, QuantityPlanned, QuantityCompleted, CompletionDate
    FROM dbo.ProductionOrders WHERE Id = @ProductionOrderId;
END;
GO

/* -------------------------------------------------------------------------------------
   usp_GetInventorySummary
   Read-only. Result set 1: one row per active product x warehouse (optionally filtered).
   Result set 2: totals by category for the same filter.
   Low stock = MinimumStock > 0 AND free <= MinimumStock (InventoryItem.IsLowStock).
   Stock value = ROUND(QuantityAvailable * Cost, 2), like ReportService.InventoryAsync.
   ------------------------------------------------------------------------------------- */
CREATE OR ALTER PROCEDURE dbo.usp_GetInventorySummary
    @WarehouseId int = NULL,
    @Category    nvarchar(50) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    SELECT p.Category, p.ProductCode, p.ProductName, p.Unit, w.Code AS WarehouseCode, w.Name AS WarehouseName,
           i.QuantityAvailable, i.QuantityReserved, i.QuantityAvailable - i.QuantityReserved AS QuantityFree,
           p.MinimumStock, p.Cost AS UnitCost, ROUND(i.QuantityAvailable * p.Cost, 2) AS StockValue,
           CAST(CASE WHEN p.MinimumStock > 0 AND i.QuantityAvailable - i.QuantityReserved <= p.MinimumStock THEN 1 ELSE 0 END AS bit) AS IsLowStock
    INTO #Rows
    FROM dbo.Inventory i
    JOIN dbo.Products p   ON p.Id = i.ProductId
    JOIN dbo.Warehouses w ON w.Id = i.WarehouseId
    WHERE p.Active = 1
      AND (@WarehouseId IS NULL OR i.WarehouseId = @WarehouseId)
      AND (@Category IS NULL OR p.Category = @Category);

    SELECT * FROM #Rows ORDER BY Category, ProductCode, WarehouseCode;

    SELECT Category,
           COUNT(*)                         AS StockLines,
           COUNT(DISTINCT ProductCode)      AS Products,
           SUM(QuantityAvailable)           AS QuantityAvailable,
           SUM(QuantityReserved)            AS QuantityReserved,
           SUM(QuantityFree)                AS QuantityFree,
           SUM(StockValue)                  AS StockValue,
           SUM(CAST(IsLowStock AS int))     AS LowStockLines
    FROM #Rows
    GROUP BY Category
    ORDER BY Category;
END;
GO
