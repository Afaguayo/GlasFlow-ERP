/* =====================================================================================
   GlasFlow ERP — 01_schema.sql
   Creates the GlasFlowERP database and all tables, keys, indexes and check constraints.

   Execution order:  01_schema.sql -> 02_seed_data.sql -> 03_stored_procedures.sql
                     -> 04_views.sql -> 05_triggers.sql -> 06_reports.sql (ad-hoc queries)

   This file is generated from the EF Core migration (dotnet ef migrations script --idempotent),
   so it is exactly the schema the API uses. It is idempotent: re-running it is safe.
   Regenerate after model changes:
     dotnet ef migrations script --idempotent -p src/GlasFlow.Infrastructure -s src/GlasFlow.Infrastructure
   ===================================================================================== */
IF DB_ID(N'GlasFlowERP') IS NULL
    CREATE DATABASE GlasFlowERP;
GO
USE GlasFlowERP;
GO

IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [Id] bigint NOT NULL IDENTITY,
        [Timestamp] datetime2 NOT NULL,
        [UserName] nvarchar(50) NOT NULL,
        [Action] nvarchar(60) NOT NULL,
        [EntityType] nvarchar(60) NOT NULL,
        [EntityId] nvarchar(60) NULL,
        [Details] nvarchar(max) NULL,
        [Source] nvarchar(20) NOT NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [Customers] (
        [Id] int NOT NULL IDENTITY,
        [CustomerCode] nvarchar(20) NOT NULL,
        [ExternalId] nvarchar(50) NULL,
        [Name] nvarchar(150) NOT NULL,
        [Rfc] nvarchar(13) NULL,
        [ContactName] nvarchar(100) NULL,
        [Email] nvarchar(150) NULL,
        [Phone] nvarchar(30) NULL,
        [Address] nvarchar(250) NULL,
        [City] nvarchar(80) NOT NULL,
        [State] nvarchar(80) NOT NULL,
        [PostalCode] nvarchar(5) NULL,
        [TaxZone] nvarchar(20) NOT NULL,
        [CreditLimit] decimal(18,2) NOT NULL,
        [Active] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Customers] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Customers_CreditLimit] CHECK (CreditLimit >= 0),
        CONSTRAINT [CK_Customers_TaxZone] CHECK (TaxZone IN ('General','FronteraNorte'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [Suppliers] (
        [Id] int NOT NULL IDENTITY,
        [SupplierCode] nvarchar(20) NOT NULL,
        [Name] nvarchar(150) NOT NULL,
        [Rfc] nvarchar(13) NULL,
        [ContactName] nvarchar(100) NULL,
        [Email] nvarchar(150) NULL,
        [Phone] nvarchar(30) NULL,
        [City] nvarchar(80) NOT NULL,
        [State] nvarchar(80) NOT NULL,
        [Active] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Suppliers] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] int NOT NULL IDENTITY,
        [Username] nvarchar(50) NOT NULL,
        [DisplayName] nvarchar(100) NOT NULL,
        [Email] nvarchar(150) NULL,
        [PasswordHash] nvarchar(200) NOT NULL,
        [Role] nvarchar(20) NOT NULL,
        [Active] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [LastLoginAt] datetime2 NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Users_Role] CHECK (Role IN ('Admin','Operator','Viewer'))
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [Warehouses] (
        [Id] int NOT NULL IDENTITY,
        [Code] nvarchar(20) NOT NULL,
        [Name] nvarchar(100) NOT NULL,
        [City] nvarchar(80) NOT NULL,
        [State] nvarchar(80) NOT NULL,
        [Active] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Warehouses] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [Products] (
        [Id] int NOT NULL IDENTITY,
        [ProductCode] nvarchar(30) NOT NULL,
        [ExternalId] nvarchar(50) NULL,
        [ProductName] nvarchar(150) NOT NULL,
        [Category] nvarchar(50) NOT NULL,
        [Description] nvarchar(500) NULL,
        [Unit] nvarchar(10) NOT NULL,
        [Cost] decimal(18,2) NOT NULL,
        [SellingPrice] decimal(18,2) NOT NULL,
        [MinimumStock] decimal(18,3) NOT NULL,
        [Active] bit NOT NULL,
        [SupplierId] int NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Products] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Products_Category] CHECK (Category IN (N'Vidrio claro',N'Vidrio templado',N'Vidrio laminado',N'Vidrio decorativo',N'Accesorios')),
        CONSTRAINT [CK_Products_Cost] CHECK (Cost >= 0),
        CONSTRAINT [CK_Products_MinimumStock] CHECK (MinimumStock >= 0),
        CONSTRAINT [CK_Products_SellingPrice] CHECK (SellingPrice > 0),
        CONSTRAINT [CK_Products_Unit] CHECK (Unit IN ('m2','pza','ml','kg')),
        CONSTRAINT [FK_Products_Suppliers_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [Suppliers] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [SalesOrders] (
        [Id] int NOT NULL IDENTITY,
        [OrderNumber] nvarchar(20) NOT NULL,
        [CustomerId] int NOT NULL,
        [WarehouseId] int NOT NULL,
        [OrderDate] datetime2 NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [TaxRate] decimal(5,4) NOT NULL,
        [Subtotal] decimal(18,2) NOT NULL,
        [Tax] decimal(18,2) NOT NULL,
        [Total] decimal(18,2) NOT NULL,
        [Notes] nvarchar(500) NULL,
        [ExternalReference] nvarchar(60) NULL,
        [ConfirmedAt] datetime2 NULL,
        [ShippedAt] datetime2 NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_SalesOrders] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_SalesOrders_Amounts] CHECK (Subtotal >= 0 AND Tax >= 0 AND Total = Subtotal + Tax),
        CONSTRAINT [CK_SalesOrders_Status] CHECK (Status IN ('Draft','Confirmed','Shipped','Cancelled')),
        CONSTRAINT [FK_SalesOrders_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SalesOrders_Warehouses_WarehouseId] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [Inventory] (
        [Id] int NOT NULL IDENTITY,
        [ProductId] int NOT NULL,
        [WarehouseId] int NOT NULL,
        [QuantityAvailable] decimal(18,3) NOT NULL,
        [QuantityReserved] decimal(18,3) NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        [Version] int NOT NULL,
        CONSTRAINT [PK_Inventory] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_Inventory_Reserved] CHECK (QuantityReserved >= 0),
        CONSTRAINT [FK_Inventory_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Inventory_Warehouses_WarehouseId] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [InventoryMovements] (
        [Id] bigint NOT NULL IDENTITY,
        [ProductId] int NOT NULL,
        [WarehouseId] int NOT NULL,
        [MovementType] nvarchar(30) NOT NULL,
        [Quantity] decimal(18,3) NOT NULL,
        [Reference] nvarchar(50) NOT NULL,
        [Notes] nvarchar(250) NULL,
        [Timestamp] datetime2 NOT NULL,
        [UserName] nvarchar(50) NOT NULL,
        CONSTRAINT [PK_InventoryMovements] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_InventoryMovements_Type] CHECK (MovementType IN ('Adjustment','Reservation','ReservationRelease','SaleShipment','ProductionReceipt','TransferOut','TransferIn','Import')),
        CONSTRAINT [FK_InventoryMovements_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_InventoryMovements_Warehouses_WarehouseId] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [Notifications] (
        [Id] int NOT NULL IDENTITY,
        [Type] nvarchar(30) NOT NULL,
        [Severity] nvarchar(20) NOT NULL,
        [Title] nvarchar(150) NOT NULL,
        [Message] nvarchar(500) NOT NULL,
        [ProductId] int NULL,
        [WarehouseId] int NULL,
        [DedupKey] nvarchar(80) NULL,
        [IsRead] bit NOT NULL,
        [IsResolved] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ResolvedAt] datetime2 NULL,
        CONSTRAINT [PK_Notifications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Notifications_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Notifications_Warehouses_WarehouseId] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [ProductionOrders] (
        [Id] int NOT NULL IDENTITY,
        [ProductionNumber] nvarchar(20) NOT NULL,
        [ProductId] int NOT NULL,
        [WarehouseId] int NOT NULL,
        [QuantityPlanned] decimal(18,3) NOT NULL,
        [QuantityCompleted] decimal(18,3) NOT NULL,
        [Status] nvarchar(20) NOT NULL,
        [DueDate] datetime2 NULL,
        [StartDate] datetime2 NULL,
        [CompletionDate] datetime2 NULL,
        [Notes] nvarchar(500) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_ProductionOrders] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_ProductionOrders_Quantities] CHECK (QuantityPlanned > 0 AND QuantityCompleted >= 0 AND QuantityCompleted <= QuantityPlanned),
        CONSTRAINT [CK_ProductionOrders_Status] CHECK (Status IN ('Planned','InProgress','Completed','Cancelled')),
        CONSTRAINT [FK_ProductionOrders_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_ProductionOrders_Warehouses_WarehouseId] FOREIGN KEY ([WarehouseId]) REFERENCES [Warehouses] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE TABLE [SalesOrderDetails] (
        [Id] int NOT NULL IDENTITY,
        [SalesOrderId] int NOT NULL,
        [ProductId] int NOT NULL,
        [Quantity] decimal(18,3) NOT NULL,
        [UnitPrice] decimal(18,2) NOT NULL,
        [LineTotal] decimal(18,2) NOT NULL,
        CONSTRAINT [PK_SalesOrderDetails] PRIMARY KEY ([Id]),
        CONSTRAINT [CK_SalesOrderDetails_Quantity] CHECK (Quantity > 0),
        CONSTRAINT [CK_SalesOrderDetails_UnitPrice] CHECK (UnitPrice > 0),
        CONSTRAINT [FK_SalesOrderDetails_Products_ProductId] FOREIGN KEY ([ProductId]) REFERENCES [Products] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_SalesOrderDetails_SalesOrders_SalesOrderId] FOREIGN KEY ([SalesOrderId]) REFERENCES [SalesOrders] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EntityType_EntityId] ON [AuditLogs] ([EntityType], [EntityId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_Timestamp] ON [AuditLogs] ([Timestamp]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Customers_CustomerCode] ON [Customers] ([CustomerCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Customers_ExternalId] ON [Customers] ([ExternalId]) WHERE ExternalId IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Customers_Name] ON [Customers] ([Name]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Inventory_ProductId_WarehouseId] ON [Inventory] ([ProductId], [WarehouseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Inventory_WarehouseId] ON [Inventory] ([WarehouseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryMovements_ProductId_Timestamp] ON [InventoryMovements] ([ProductId], [Timestamp]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryMovements_Reference] ON [InventoryMovements] ([Reference]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_InventoryMovements_WarehouseId_Timestamp] ON [InventoryMovements] ([WarehouseId], [Timestamp]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Notifications_DedupKey] ON [Notifications] ([DedupKey]) WHERE DedupKey IS NOT NULL AND IsResolved = 0');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_IsRead_CreatedAt] ON [Notifications] ([IsRead], [CreatedAt]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_ProductId] ON [Notifications] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Notifications_WarehouseId] ON [Notifications] ([WarehouseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProductionOrders_ProductId] ON [ProductionOrders] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_ProductionOrders_ProductionNumber] ON [ProductionOrders] ([ProductionNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProductionOrders_Status_DueDate] ON [ProductionOrders] ([Status], [DueDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_ProductionOrders_WarehouseId] ON [ProductionOrders] ([WarehouseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Products_Category] ON [Products] ([Category]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_Products_ExternalId] ON [Products] ([ExternalId]) WHERE ExternalId IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Products_ProductCode] ON [Products] ([ProductCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Products_SupplierId] ON [Products] ([SupplierId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SalesOrderDetails_ProductId] ON [SalesOrderDetails] ([ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SalesOrderDetails_SalesOrderId_ProductId] ON [SalesOrderDetails] ([SalesOrderId], [ProductId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SalesOrders_CustomerId_OrderDate] ON [SalesOrders] ([CustomerId], [OrderDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [IX_SalesOrders_ExternalReference] ON [SalesOrders] ([ExternalReference]) WHERE ExternalReference IS NOT NULL');
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_SalesOrders_OrderNumber] ON [SalesOrders] ([OrderNumber]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SalesOrders_Status_OrderDate] ON [SalesOrders] ([Status], [OrderDate]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_SalesOrders_WarehouseId] ON [SalesOrders] ([WarehouseId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Suppliers_SupplierCode] ON [Suppliers] ([SupplierCode]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Users_Username] ON [Users] ([Username]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Warehouses_Code] ON [Warehouses] ([Code]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261026024915_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261026024915_InitialCreate', N'10.0.14');
END;

COMMIT;
GO

