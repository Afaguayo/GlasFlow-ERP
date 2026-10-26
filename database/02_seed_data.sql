/* =====================================================================================
   GlasFlow ERP — 02_seed_data.sql
   Fictional demo data for a glass manufacturer in Chihuahua, Mexico. All companies, RFCs,
   people, e-mails and phone numbers are invented (e-mails use the reserved .example domain).

   Execution order:  01_schema.sql -> 02_seed_data.sql -> 03_stored_procedures.sql
                     -> 04_views.sql -> 05_triggers.sql -> 06_reports.sql (ad-hoc queries)

   Run against the target database (no USE statement on purpose):
     sqlcmd -S .\SQLEXPRESS -E -C -b -d GlasFlowERP -i database\02_seed_data.sql

   Idempotent / re-runnable:
   1. Master data (warehouses, suppliers, customers, products, one Inventory row per product x
      warehouse) is inserted by business code only when missing — the SAME codes, ExternalIds,
      prices and minimums as src/GlasFlow.Infrastructure/Persistence/DataSeeder.cs, so the API and
      this script produce interchangeable catalogs. Existing rows are never overwritten.
   2. The transactional sample (10 sales orders, 4 production orders, stock quantities and their
      movements) is written only when dbo.SalesOrders is empty — the same rule DataSeeder uses —
      so a second run, or a run against a database the API already seeded, adds nothing.
      (If this script seeds first, the API later sees orders and skips its own history.)
   Users are NOT inserted: password hashes are created by the application (PBKDF2) on start-up.

   Consistency of the sample: for every product x warehouse
     Import 'SALDO-INICIAL' + ProductionReceipt + SaleShipment (negative) = Inventory.QuantityAvailable
     SUM(lines of Confirmed orders)                                       = Inventory.QuantityReserved
   ('Reservation' movements are informational and do not change on-hand stock.) 06_reports.sql
   contains a query that checks the first equation. Several rows are deliberately left at or below
   MinimumStock so low-stock alerts and reports have content; run EXEC dbo.usp_RunLowStockCheck
   (from 03) afterwards to create the corresponding notifications.
   Dates are relative to the moment the script runs (UTC), so the data always looks recent.
   ===================================================================================== */
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

/* ---------------------------------------------------------------- Master data */
BEGIN TRANSACTION;

DECLARE @Created datetime2 = DATEADD(month, -12, SYSUTCDATETIME());

-- Warehouses
INSERT dbo.Warehouses (Code, Name, City, State, Active, CreatedAt, UpdatedAt)
SELECT v.Code, v.Name, v.City, v.State, 1, @Created, @Created
FROM (VALUES
    (N'CUU-01', N'Planta Chihuahua – Complejo Industrial', N'Chihuahua', N'Chihuahua'),
    (N'CJS-01', N'CEDIS Ciudad Juárez', N'Ciudad Juárez', N'Chihuahua')
) v (Code, Name, City, State)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Warehouses w WHERE w.Code = v.Code);

-- Suppliers (e-mail / phone pattern identical to DataSeeder: index i = 1..4)
INSERT dbo.Suppliers (SupplierCode, Name, Rfc, ContactName, Email, Phone, City, State, Active, CreatedAt, UpdatedAt)
SELECT v.Code, v.Name, v.Rfc, v.Contact,
       CONCAT(N'ventas', v.i, N'@proveedor-', v.i, N'.example'),
       CONCAT(N'81 5550 01', RIGHT(CONCAT(N'00', v.i), 2)),
       v.City, v.State, 1, @Created, @Created
FROM (VALUES
    (1, N'PRV-001', N'Cristales Flotados del Norte S.A. de C.V.', N'CFN050412AB1', N'Ing. Ramiro Fuentes', N'Monterrey', N'Nuevo León'),
    (2, N'PRV-002', N'Herrajes y Perfiles Industriales S.A. de C.V.', N'HPI100823CD2', N'Lic. Sofía Treviño', N'Saltillo', N'Coahuila'),
    (3, N'PRV-003', N'Películas PVB de Occidente S.A. de C.V.', N'PPO120301EF3', N'Ing. Marco Ibarra', N'Guadalajara', N'Jalisco'),
    (4, N'PRV-004', N'Selladores y Químicos del Bajío S.A. de C.V.', N'SQB090917GH4', N'C.P. Laura Méndez', N'León', N'Guanajuato')
) v (i, Code, Name, Rfc, Contact, City, State)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Suppliers s WHERE s.SupplierCode = v.Code);

-- Customers (ExternalId ERP-C{1000+i}, e-mail compras@cliente{i:00}.example, phone 614 555 {i:0000})
INSERT dbo.Customers (CustomerCode, ExternalId, Name, Rfc, ContactName, Email, Phone, Address, City, State, PostalCode,
                      TaxZone, CreditLimit, Active, CreatedAt, UpdatedAt)
SELECT v.Code, CONCAT(N'ERP-C', 1000 + v.i), v.Name, v.Rfc, v.Contact,
       CONCAT(N'compras@cliente', RIGHT(CONCAT(N'00', v.i), 2), N'.example'),
       CONCAT(N'614 555 ', RIGHT(CONCAT(N'0000', v.i), 4)),
       v.Address, v.City, v.State, v.Cp, v.Zone, v.Credit, 1, @Created, @Created
FROM (VALUES
    ( 1, N'CLI-001', N'Constructora Cumbres del Norte S.A. de C.V.', N'CCN100315AB1', N'Ing. Alejandro Rivas', N'Av. Tecnológico 4520, Col. Las Granjas', N'Chihuahua', N'Chihuahua', N'31100', N'General', 750000.00),
    ( 2, N'CLI-002', N'Aluminio y Cristal Juárez S. de R.L. de C.V.', N'ACJ120601KL2', N'Sra. Patricia Holguín', N'Blvd. Tomás Fernández 7810', N'Ciudad Juárez', N'Chihuahua', N'32460', N'FronteraNorte', 400000.00),
    ( 3, N'CLI-003', N'Desarrollos Inmobiliarios Sierra Madre S.A. de C.V.', N'DIS150920MN3', N'Arq. Fernanda Loya', N'Periférico de la Juventud 3100', N'Chihuahua', N'Chihuahua', N'31207', N'General', 1200000.00),
    ( 4, N'CLI-004', N'Vidriería La Paz de Delicias', N'VLP081105QR4', N'Sr. Jesús Armendáriz', N'Av. 6a Oriente 215, Centro', N'Delicias', N'Chihuahua', N'33000', N'General', 150000.00),
    ( 5, N'CLI-005', N'Cancelería Moderna del Desierto S.A. de C.V.', N'CMD170210ST5', N'Sr. Óscar Villalobos', N'Av. de las Torres 1450', N'Ciudad Juárez', N'Chihuahua', N'32575', N'FronteraNorte', 300000.00),
    ( 6, N'CLI-006', N'Hotelera Plaza Real del Centro S.A. de C.V.', N'HPR990712UV6', N'Lic. Mariana Quintana', N'Av. Independencia 1100, Centro', N'Chihuahua', N'Chihuahua', N'31000', N'General', 500000.00),
    ( 7, N'CLI-007', N'Arquitectura y Fachadas Bravo S.C.', N'AFB130418WX7', N'Arq. Daniel Bravo', N'Av. Paseo Triunfo de la República 3200', N'Ciudad Juárez', N'Chihuahua', N'32330', N'FronteraNorte', 650000.00),
    ( 8, N'CLI-008', N'Muebles y Espejos de Parral', N'MEP110830YZ8', N'Sra. Rosa Chávez', N'Calle Mercaderes 45, Centro', N'Hidalgo del Parral', N'Chihuahua', N'33800', N'General', 120000.00),
    ( 9, N'CLI-009', N'Grupo Constructor Valle de Cuauhtémoc S.A. de C.V.', N'GCV160505AB9', N'Ing. Peter Wiebe', N'Calle Allende 1020', N'Cuauhtémoc', N'Chihuahua', N'31500', N'General', 450000.00),
    (10, N'CLI-010', N'Torres Ejecutivas del Norte S.A. de C.V.', N'TEN180922CD1', N'Lic. Ricardo Garza', N'Av. Constitución 2050, Centro', N'Monterrey', N'Nuevo León', N'64000', N'General', 900000.00),
    (11, N'CLI-011', N'Remodelaciones Hogar Feliz', N'RHF190314EF2', N'Sra. Lucía Domínguez', N'Calle Victoria 820, Centro', N'Chihuahua', N'Chihuahua', N'31000', N'General', 80000.00),
    (12, N'CLI-012', N'Cristalería Industrial de Torreón S.A. de C.V.', N'CIT140727GH3', N'Ing. Héctor Salas', N'Blvd. Revolución 1500', N'Torreón', N'Coahuila', N'27000', N'General', 350000.00)
) v (i, Code, Name, Rfc, Contact, Address, City, State, Cp, Zone, Credit)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Customers c WHERE c.CustomerCode = v.Code);

-- Products (ExternalId SKU-{20000 + i*7}; supplier resolved by SupplierCode)
INSERT dbo.Products (ProductCode, ExternalId, ProductName, Category, Description, Unit, Cost, SellingPrice, MinimumStock,
                     Active, SupplierId, CreatedAt, UpdatedAt)
SELECT v.Code, CONCAT(N'SKU-', 20000 + v.i * 7), v.Name, v.Category, v.Description, v.Unit, v.Cost, v.Price, v.MinStock,
       1, s.Id, @Created, @Created
FROM (VALUES
    ( 1, N'CLR-4MM',  N'Vidrio claro flotado 4 mm',                 N'Vidrio claro',      N'm2',  210.00,  340.00, 120.000, N'PRV-001', N'Hoja de vidrio flotado claro para ventanería residencial.'),
    ( 2, N'CLR-6MM',  N'Vidrio claro flotado 6 mm',                 N'Vidrio claro',      N'm2',  300.00,  480.00, 100.000, N'PRV-001', N'Vidrio claro para cancelería y aparadores.'),
    ( 3, N'CLR-10MM', N'Vidrio claro flotado 10 mm',                N'Vidrio claro',      N'm2',  520.00,  820.00,  40.000, N'PRV-001', N'Vidrio grueso para cubiertas y mostradores.'),
    ( 4, N'TMP-6MM',  N'Vidrio templado 6 mm',                      N'Vidrio templado',   N'm2',  620.00,  980.00,  60.000, N'PRV-001', N'Templado térmicamente; ventanas y puertas de seguridad.'),
    ( 5, N'TMP-10MM', N'Vidrio templado 10 mm',                     N'Vidrio templado',   N'm2',  890.00, 1450.00,  50.000, N'PRV-001', N'Canceles de baño y puertas de vidrio templado.'),
    ( 6, N'TMP-12MM', N'Vidrio templado 12 mm para barandal',       N'Vidrio templado',   N'm2', 1100.00, 1780.00,  30.000, N'PRV-001', N'Barandales y fachadas con herrajes de punto fijo.'),
    ( 7, N'LAM-6MM',  N'Vidrio laminado 3+3 mm PVB claro',          N'Vidrio laminado',   N'm2',  760.00, 1190.00,  50.000, N'PRV-003', N'Laminado de seguridad para ventanas y domos.'),
    ( 8, N'LAM-8MM',  N'Vidrio laminado 4+4 mm seguridad',          N'Vidrio laminado',   N'm2',  940.00, 1490.00,  40.000, N'PRV-003', N'Laminado para fachadas y zonas de alto tránsito.'),
    ( 9, N'LAM-ACU',  N'Vidrio laminado acústico 5+5 mm',           N'Vidrio laminado',   N'm2', 1350.00, 2150.00,  20.000, N'PRV-003', N'Laminado con PVB acústico para oficinas y hoteles.'),
    (10, N'DEC-ESM',  N'Vidrio esmerilado 6 mm',                    N'Vidrio decorativo', N'm2',  480.00,  760.00,  40.000, N'PRV-001', N'Acabado satinado al ácido para privacidad.'),
    (11, N'DEC-REF',  N'Vidrio reflejante bronce 6 mm',             N'Vidrio decorativo', N'm2',  520.00,  840.00,  30.000, N'PRV-001', N'Control solar para fachadas comerciales.'),
    (12, N'DEC-ESP',  N'Espejo plata 4 mm',                         N'Vidrio decorativo', N'm2',  380.00,  620.00,  60.000, N'PRV-001', N'Espejo con capa de plata y pintura protectora.'),
    (13, N'ACC-SIL',  N'Silicón estructural transparente 300 ml',   N'Accesorios',        N'pza',  85.00,  145.00, 200.000, N'PRV-004', N'Sellador de silicón neutro para vidrio y aluminio.'),
    (14, N'ACC-BIS',  N'Bisagra hidráulica de acero inoxidable',    N'Accesorios',        N'pza', 650.00, 1090.00,  40.000, N'PRV-002', N'Bisagra para puertas de vidrio templado de 10–12 mm.'),
    (15, N'ACC-PERF', N'Perfil de aluminio para cancel 6 m',        N'Accesorios',        N'pza', 420.00,  690.00,  60.000, N'PRV-002', N'Perfil natural anodizado para canceles corredizos.'),
    (16, N'ACC-ARA',  N'Araña de acero inoxidable 4 puntos',        N'Accesorios',        N'pza', 980.00, 1590.00,  20.000, N'PRV-002', N'Herraje de punto fijo para fachadas de vidrio.')
) v (i, Code, Name, Category, Unit, Cost, Price, MinStock, SupplierCode, Description)
LEFT JOIN dbo.Suppliers s ON s.SupplierCode = v.SupplierCode
WHERE NOT EXISTS (SELECT 1 FROM dbo.Products p WHERE p.ProductCode = v.Code);

-- One stock row per product x warehouse (quantities are set by the transactional block below).
INSERT dbo.Inventory (ProductId, WarehouseId, QuantityAvailable, QuantityReserved, UpdatedAt, Version)
SELECT p.Id, w.Id, 0, 0, @Created, 0
FROM dbo.Products p
CROSS JOIN dbo.Warehouses w
WHERE NOT EXISTS (SELECT 1 FROM dbo.Inventory i WHERE i.ProductId = p.Id AND i.WarehouseId = w.Id);

COMMIT TRANSACTION;
GO

/* ---------------------------------------------------------------- Transactional sample */
IF EXISTS (SELECT 1 FROM dbo.SalesOrders)
BEGIN
    PRINT N'dbo.SalesOrders already has data: transactional sample skipped (idempotent re-run).';
    RETURN;
END;

BEGIN TRANSACTION;

DECLARE @Now   datetime2 = SYSUTCDATETIME();
DECLARE @Today datetime2 = CAST(CAST(@Now AS date) AS datetime2);
DECLARE @SeedUser nvarchar(50) = N'seed';

/* Sales orders: Seq, customer, days ago, hour of day, status */
DECLARE @Orders TABLE
(
    Seq int PRIMARY KEY, CustomerCode nvarchar(20), DaysAgo int, HourOfDay int, Status nvarchar(20), Notes nvarchar(500),
    CustomerId int NULL, WarehouseId int NULL, TaxRate decimal(5,4) NULL, OrderDate datetime2 NULL,
    ConfirmedAt datetime2 NULL, ShippedAt datetime2 NULL, OrderNumber nvarchar(20) NULL,
    Subtotal decimal(18,2) NULL, Tax decimal(18,2) NULL, SalesOrderId int NULL
);
INSERT @Orders (Seq, CustomerCode, DaysAgo, HourOfDay, Status, Notes) VALUES
    ( 1, N'CLI-001', 150,  9, N'Shipped',   N'Fraccionamiento Cumbres etapa 3: ventanería.'),
    ( 2, N'CLI-002', 131, 11, N'Shipped',   N'Canceles de baño, entrega en CEDIS Juárez.'),
    ( 3, N'CLI-003', 112, 10, N'Shipped',   N'Torre Sierra Madre: fachada y barandales.'),
    ( 4, N'CLI-006',  86, 13, N'Shipped',   N'Remodelación de habitaciones (vidrio acústico).'),
    ( 5, N'CLI-005',  61, 16, N'Shipped',   NULL),
    ( 6, N'CLI-010',  40,  9, N'Shipped',   N'Barandal de vidrio templado, piso 12.'),
    ( 7, N'CLI-007',  19, 12, N'Confirmed', N'Fachada comercial, programar entrega.'),
    ( 8, N'CLI-004',  10, 15, N'Confirmed', NULL),
    ( 9, N'CLI-009',   5, 10, N'Draft',     N'Cotización en revisión por el cliente.'),
    (10, N'CLI-012',   2, 17, N'Draft',     NULL);

DECLARE @OrderLines TABLE (Seq int, ProductCode nvarchar(30), Quantity decimal(18,3),
                           ProductId int NULL, UnitPrice decimal(18,2) NULL, LineTotal decimal(18,2) NULL,
                           PRIMARY KEY (Seq, ProductCode));
INSERT @OrderLines (Seq, ProductCode, Quantity) VALUES
    ( 1, N'CLR-6MM',   80.5), ( 1, N'ACC-SIL',  40),
    ( 2, N'TMP-10MM',  45),   ( 2, N'ACC-BIS',  12),
    ( 3, N'LAM-8MM',   60),   ( 3, N'TMP-12MM', 25.5), ( 3, N'ACC-ARA', 16),
    ( 4, N'LAM-ACU',   18),   ( 4, N'DEC-ESP',  30),
    ( 5, N'CLR-4MM',  120),   ( 5, N'ACC-PERF', 20),
    ( 6, N'TMP-12MM',  40),   ( 6, N'ACC-ARA',   8),
    ( 7, N'LAM-6MM',   35),   ( 7, N'TMP-6MM',  20.5),
    ( 8, N'DEC-ESM',   25),   ( 8, N'ACC-SIL',  60),
    ( 9, N'CLR-10MM',  15),
    (10, N'TMP-10MM',  30),   (10, N'ACC-BIS',   8);

-- Header facts: customer, warehouse by tax zone (border customers ship from CJS-01), IVA rate, dates.
UPDATE o
   SET CustomerId  = c.Id,
       WarehouseId = w.Id,
       TaxRate     = CASE WHEN c.TaxZone = N'FronteraNorte' THEN 0.08 ELSE 0.16 END,
       OrderDate   = DATEADD(hour, o.HourOfDay, DATEADD(day, -o.DaysAgo, @Today))
FROM @Orders o
JOIN dbo.Customers c  ON c.CustomerCode = o.CustomerCode
JOIN dbo.Warehouses w ON w.Code = CASE WHEN c.TaxZone = N'FronteraNorte' THEN N'CJS-01' ELSE N'CUU-01' END;

UPDATE @Orders
   SET ConfirmedAt = CASE WHEN Status IN (N'Confirmed', N'Shipped') THEN DATEADD(hour, 2, OrderDate) END,
       ShippedAt   = CASE WHEN Status = N'Shipped' THEN DATEADD(day, 2, OrderDate) END;

-- SO-YYYY-NNNN, numbered by order date within each year (the table is empty, so the sequence starts at 1).
WITH n AS (
    SELECT OrderNumber, OrderDate,
           ROW_NUMBER() OVER (PARTITION BY YEAR(OrderDate) ORDER BY OrderDate, Seq) AS rn
    FROM @Orders
)
UPDATE n SET OrderNumber = CONCAT(N'SO-', YEAR(OrderDate), N'-', RIGHT(CONCAT(N'0000', rn), 4));

-- Lines at list price; LineTotal = ROUND(qty * price, 2).
UPDATE l
   SET ProductId = p.Id, UnitPrice = p.SellingPrice, LineTotal = ROUND(l.Quantity * p.SellingPrice, 2)
FROM @OrderLines l
JOIN dbo.Products p ON p.ProductCode = l.ProductCode;

-- Subtotal = sum of lines, Tax = ROUND(Subtotal * rate, 2) (16% General, 8% FronteraNorte).
UPDATE o
   SET Subtotal = s.Subtotal, Tax = ROUND(s.Subtotal * o.TaxRate, 2)
FROM @Orders o
JOIN (SELECT Seq, SUM(LineTotal) AS Subtotal FROM @OrderLines GROUP BY Seq) s ON s.Seq = o.Seq;

INSERT dbo.SalesOrders (OrderNumber, CustomerId, WarehouseId, OrderDate, Status, TaxRate, Subtotal, Tax, Total,
                        Notes, ExternalReference, ConfirmedAt, ShippedAt, CreatedAt, UpdatedAt)
SELECT OrderNumber, CustomerId, WarehouseId, OrderDate, Status, TaxRate, Subtotal, Tax, Subtotal + Tax,
       Notes, NULL, ConfirmedAt, ShippedAt, OrderDate, COALESCE(ShippedAt, ConfirmedAt, OrderDate)
FROM @Orders
ORDER BY Seq;   -- Seq is in date order

UPDATE o SET SalesOrderId = so.Id
FROM @Orders o JOIN dbo.SalesOrders so ON so.OrderNumber = o.OrderNumber;

INSERT dbo.SalesOrderDetails (SalesOrderId, ProductId, Quantity, UnitPrice, LineTotal)
SELECT o.SalesOrderId, l.ProductId, l.Quantity, l.UnitPrice, l.LineTotal
FROM @OrderLines l
JOIN @Orders o ON o.Seq = l.Seq;

-- Reservation movements (informational) for every order that was confirmed, then shipment movements.
INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
SELECT l.ProductId, o.WarehouseId, N'Reservation', l.Quantity, o.OrderNumber, N'Reserved for order', o.ConfirmedAt, @SeedUser
FROM @OrderLines l JOIN @Orders o ON o.Seq = l.Seq
WHERE o.ConfirmedAt IS NOT NULL;

INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
SELECT l.ProductId, o.WarehouseId, N'SaleShipment', -l.Quantity, o.OrderNumber, NULL, o.ShippedAt, @SeedUser
FROM @OrderLines l JOIN @Orders o ON o.Seq = l.Seq
WHERE o.Status = N'Shipped';

/* Production orders */
DECLARE @Prod TABLE
(
    Seq int PRIMARY KEY, ProductCode nvarchar(30), WarehouseCode nvarchar(20), Planned decimal(18,3), Done decimal(18,3),
    Status nvarchar(20), CreatedDaysAgo int, StartedDaysAgo int NULL, CompletedDaysAgo int NULL, DueInDays int, Notes nvarchar(500),
    ProductId int NULL, WarehouseId int NULL, ProductionNumber nvarchar(20) NULL, CreatedAt datetime2 NULL,
    StartDate datetime2 NULL, CompletionDate datetime2 NULL, DueDate datetime2 NULL, ReceivedAt datetime2 NULL
);
INSERT @Prod (Seq, ProductCode, WarehouseCode, Planned, Done, Status, CreatedDaysAgo, StartedDaysAgo, CompletedDaysAgo, DueInDays, Notes) VALUES
    (1, N'TMP-10MM', N'CUU-01', 100, 100, N'Completed', 124, 122, 116, -115, N'Lote para canceles de baño.'),
    (2, N'LAM-8MM',  N'CJS-01',  80,  76, N'Completed',  72,  70,  60,  -64, N'Cerrada con 4 m2 de merma.'),
    (3, N'DEC-ESP',  N'CUU-01', 120,  50, N'InProgress', 10,   8, NULL,   5, NULL),
    (4, N'LAM-ACU',  N'CUU-01',  60,   0, N'Planned',     1, NULL, NULL, 14, N'Reposición de inventario bajo mínimo.');

UPDATE pr
   SET ProductId = p.Id, WarehouseId = w.Id,
       CreatedAt      = DATEADD(hour, 8, DATEADD(day, -pr.CreatedDaysAgo, @Today)),
       StartDate      = CASE WHEN pr.StartedDaysAgo IS NOT NULL THEN DATEADD(hour, 7, DATEADD(day, -pr.StartedDaysAgo, @Today)) END,
       CompletionDate = CASE WHEN pr.CompletedDaysAgo IS NOT NULL THEN DATEADD(hour, 18, DATEADD(day, -pr.CompletedDaysAgo, @Today)) END,
       DueDate        = DATEADD(day, pr.DueInDays, @Today)
FROM @Prod pr
JOIN dbo.Products p   ON p.ProductCode = pr.ProductCode
JOIN dbo.Warehouses w ON w.Code = pr.WarehouseCode;

UPDATE @Prod SET ReceivedAt = CASE WHEN Status = N'Completed' THEN CompletionDate
                                   WHEN Done > 0 THEN DATEADD(hour, -3, @Now) END;

WITH n AS (
    SELECT ProductionNumber, CreatedAt, ROW_NUMBER() OVER (PARTITION BY YEAR(CreatedAt) ORDER BY CreatedAt, Seq) AS rn
    FROM @Prod
)
UPDATE n SET ProductionNumber = CONCAT(N'PO-', YEAR(CreatedAt), N'-',
                                       RIGHT(CONCAT(N'0000', rn + ISNULL((SELECT COUNT(*) FROM dbo.ProductionOrders x
                                                                          WHERE x.ProductionNumber LIKE CONCAT(N'PO-', YEAR(n.CreatedAt), N'-%')), 0)), 4));

INSERT dbo.ProductionOrders (ProductionNumber, ProductId, WarehouseId, QuantityPlanned, QuantityCompleted, Status,
                             DueDate, StartDate, CompletionDate, Notes, CreatedAt, UpdatedAt)
SELECT ProductionNumber, ProductId, WarehouseId, Planned, Done, Status, DueDate, StartDate, CompletionDate, Notes,
       CreatedAt, COALESCE(ReceivedAt, StartDate, CreatedAt)
FROM @Prod
ORDER BY Seq;     -- Seq is in date order

INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
SELECT ProductId, WarehouseId, N'ProductionReceipt', Done, ProductionNumber, NULL, ReceivedAt, @SeedUser
FROM @Prod
WHERE Done > 0;

/* Final stock and opening balances.
   Free-stock target per row: the same five deliberate low-stock rows as DataSeeder.LowStockTargets,
   everything else a deterministic 1.4x - 3.6x of the minimum. Then
     reserved  = lines of Confirmed orders
     available = free + reserved
     opening   = available + shipped - produced   (raised to 0 if negative, with available raised to match) */
DECLARE @Stock TABLE (InventoryId int PRIMARY KEY, ProductId int, WarehouseId int, FreeTarget decimal(18,3),
                      Reserved decimal(18,3), Shipped decimal(18,3), Produced decimal(18,3),
                      Available decimal(18,3) NULL, Opening decimal(18,3) NULL);

INSERT @Stock (InventoryId, ProductId, WarehouseId, FreeTarget, Reserved, Shipped, Produced)
SELECT i.Id, i.ProductId, i.WarehouseId,
       COALESCE(t.FreeTarget, ROUND(p.MinimumStock * (1.4 + ((p.Id * 37 + i.WarehouseId * 11) % 23) / 10.0), 0)),
       ISNULL((SELECT SUM(l.Quantity) FROM @OrderLines l JOIN @Orders o ON o.Seq = l.Seq
               WHERE o.Status = N'Confirmed' AND l.ProductId = i.ProductId AND o.WarehouseId = i.WarehouseId), 0),
       ISNULL((SELECT SUM(l.Quantity) FROM @OrderLines l JOIN @Orders o ON o.Seq = l.Seq
               WHERE o.Status = N'Shipped' AND l.ProductId = i.ProductId AND o.WarehouseId = i.WarehouseId), 0),
       ISNULL((SELECT SUM(pr.Done) FROM @Prod pr WHERE pr.ProductId = i.ProductId AND pr.WarehouseId = i.WarehouseId), 0)
FROM dbo.Inventory i
JOIN dbo.Products p   ON p.Id = i.ProductId
JOIN dbo.Warehouses w ON w.Id = i.WarehouseId
LEFT JOIN (VALUES
    (N'TMP-12MM', N'CJS-01', 12.0),
    (N'LAM-ACU',  N'CUU-01',  8.0),
    (N'LAM-ACU',  N'CJS-01',  0.0),
    (N'ACC-BIS',  N'CUU-01', 25.0),
    (N'CLR-10MM', N'CJS-01', 30.0)
) t (ProductCode, WarehouseCode, FreeTarget) ON t.ProductCode = p.ProductCode AND t.WarehouseCode = w.Code;

UPDATE @Stock SET Available = FreeTarget + Reserved;
UPDATE @Stock SET Opening = Available + Shipped - Produced;
UPDATE @Stock SET Available = Available - Opening, Opening = 0 WHERE Opening < 0;

UPDATE i
   SET QuantityAvailable = s.Available, QuantityReserved = s.Reserved, UpdatedAt = @Now, Version = i.Version + 1
FROM dbo.Inventory i
JOIN @Stock s ON s.InventoryId = i.Id;

INSERT dbo.InventoryMovements (ProductId, WarehouseId, MovementType, Quantity, Reference, Notes, [Timestamp], UserName)
SELECT ProductId, WarehouseId, N'Import', Opening, N'SALDO-INICIAL', N'Saldo inicial migrado del sistema anterior',
       DATEADD(month, -12, DATEFROMPARTS(YEAR(@Now), MONTH(@Now), 1)), @SeedUser
FROM @Stock
WHERE Opening > 0;

INSERT dbo.AuditLogs ([Timestamp], UserName, Action, EntityType, EntityId, Details, Source)
SELECT @Now, @SeedUser, N'DatabaseSeeded', N'System', NULL,
       CONCAT((SELECT COUNT(*) FROM @Orders), N' sales orders, ', (SELECT COUNT(*) FROM @Prod), N' production orders, ',
              (SELECT COUNT(*) FROM dbo.InventoryMovements), N' movements (02_seed_data.sql)'),
       N'DB_SEED';

COMMIT TRANSACTION;
PRINT N'Transactional sample inserted.';
GO
