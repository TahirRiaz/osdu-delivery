/*
  One-time transfer of the TTDB tables from OLD production DWH to the new prod DWH.

  Scope: exactly the six arc.TTDB_* tables referenced by the DWH views that were
  missing in the new estate (arc.V_Fact_TTDB_Passing, edw.V_MpcTripSummary,
  edw.V_MpcTripSummary_Tidebuss). No other TTDB_* table is copied.

  Source: [OLDPROD].[dw-dwh-prod].[arc].*  (linked server on dw-mi-sql-prod)
  Target: dw-dwh-prod on dw-mi-sql-prod, schema arc

  There is no producer flow for these tables in V3: this is a static, run-once
  operator step, the same treatment as a man-schema reference table. Re-running is
  safe: each table is only created and filled when it does not already hold rows.

  Shapes are generated from the old catalog, so column names, order, types,
  nullability and the index/key layout match old production exactly.
*/

SET NOCOUNT ON;
SET XACT_ABORT ON;

-------------------------------------------------------------------------------
-- arc.TTDB_Lines
-------------------------------------------------------------------------------
IF OBJECT_ID('arc.TTDB_Lines','U') IS NULL
BEGIN
    CREATE TABLE arc.[TTDB_Lines] (
        [Id] int NOT NULL,
        [Name] nvarchar(50) NULL,
        [TransportMode] nvarchar(50) NULL,
        [TransportSubMode] nvarchar(50) NULL,
        [PublicCode] nvarchar(50) NULL,
        [PrivateCode] nvarchar(50) NULL,
        [OperatorId] int NULL,
        [GroupOfLinesId] int NULL,
        [LineRef] nvarchar(100) NULL,
        [ImportFileId] int NULL,
        [OperatorRef] nvarchar(100) NULL,
        [RepresentedByGroupRef] nvarchar(200) NULL,
        [NetworkId] int NULL,
        [RegionCode] nvarchar(10) NULL,
        [PresentationColour] nvarchar(50) NULL,
        [PresentationTextColour] nvarchar(50) NULL,
        [InsertedDate_DW] datetime NULL,
        [UpdatedDate_DW] datetime NULL,
        CONSTRAINT [PK_TTDB_Lines] PRIMARY KEY CLUSTERED ([Id])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM arc.[TTDB_Lines])
BEGIN
    INSERT INTO arc.[TTDB_Lines] ([Id], [Name], [TransportMode], [TransportSubMode], [PublicCode], [PrivateCode], [OperatorId], [GroupOfLinesId], [LineRef], [ImportFileId], [OperatorRef], [RepresentedByGroupRef], [NetworkId], [RegionCode], [PresentationColour], [PresentationTextColour], [InsertedDate_DW], [UpdatedDate_DW])
    SELECT [Id], [Name], [TransportMode], [TransportSubMode], [PublicCode], [PrivateCode], [OperatorId], [GroupOfLinesId], [LineRef], [ImportFileId], [OperatorRef], [RepresentedByGroupRef], [NetworkId], [RegionCode], [PresentationColour], [PresentationTextColour], [InsertedDate_DW], [UpdatedDate_DW]
    FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Lines];
END
GO


-------------------------------------------------------------------------------
-- arc.TTDB_Quays
-------------------------------------------------------------------------------
IF OBJECT_ID('arc.TTDB_Quays','U') IS NULL
BEGIN
    CREATE TABLE arc.[TTDB_Quays] (
        [Id] int NOT NULL,
        [QuayRef] nvarchar(200) NULL,
        [Description] nvarchar(100) NULL,
        [PrivateCode] nvarchar(50) NULL,
        [Longitude] decimal(9,6) NULL,
        [Latitude] decimal(9,6) NULL,
        [ImportFileId] int NULL,
        [NsrId] int NULL,
        [KolumbusId] int NULL,
        [PublicCode] nvarchar(50) NULL,
        [TransportMode] nvarchar(20) NULL,
        [RegionCode] nvarchar(10) NULL,
        [Discontinued] bit NULL,
        [Changed] datetime NULL,
        [TransportSubmode] nvarchar(50) NULL,
        [InsertedDate_DW] datetime NULL,
        [UpdatedDate_DW] datetime NULL,
        CONSTRAINT [PK_TTDB_Quays] PRIMARY KEY CLUSTERED ([Id])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM arc.[TTDB_Quays])
BEGIN
    INSERT INTO arc.[TTDB_Quays] ([Id], [QuayRef], [Description], [PrivateCode], [Longitude], [Latitude], [ImportFileId], [NsrId], [KolumbusId], [PublicCode], [TransportMode], [RegionCode], [Discontinued], [Changed], [TransportSubmode], [InsertedDate_DW], [UpdatedDate_DW])
    SELECT [Id], [QuayRef], [Description], [PrivateCode], [Longitude], [Latitude], [ImportFileId], [NsrId], [KolumbusId], [PublicCode], [TransportMode], [RegionCode], [Discontinued], [Changed], [TransportSubmode], [InsertedDate_DW], [UpdatedDate_DW]
    FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Quays];
END
GO


-------------------------------------------------------------------------------
-- arc.TTDB_Quay
-------------------------------------------------------------------------------
IF OBJECT_ID('arc.TTDB_Quay','U') IS NULL
BEGIN
    CREATE TABLE arc.[TTDB_Quay] (
        [quayPK] int NOT NULL,
        [quayNsr] int NULL,
        [quayName] varchar(100) NULL,
        [lat] float NULL,
        [lon] float NULL,
        [stopPlaceNsr] int NULL,
        [stopPlaceName] varchar(50) NULL,
        [stopId] int NULL,
        [quayBK] int NULL,
        [created] date NULL,
        [updated_at] date NULL,
        [cs] int NULL,
        [dw_valid_from] date NULL,
        [dw_valid_to] date NULL,
        [InsertedDate_DW] datetime NULL,
        [UpdatedDate_DW] datetime NULL,
        CONSTRAINT [PK_TTDB_Quay] PRIMARY KEY NONCLUSTERED ([quayPK])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM arc.[TTDB_Quay])
BEGIN
    INSERT INTO arc.[TTDB_Quay] ([quayPK], [quayNsr], [quayName], [lat], [lon], [stopPlaceNsr], [stopPlaceName], [stopId], [quayBK], [created], [updated_at], [cs], [dw_valid_from], [dw_valid_to], [InsertedDate_DW], [UpdatedDate_DW])
    SELECT [quayPK], [quayNsr], [quayName], [lat], [lon], [stopPlaceNsr], [stopPlaceName], [stopId], [quayBK], [created], [updated_at], [cs], [dw_valid_from], [dw_valid_to], [InsertedDate_DW], [UpdatedDate_DW]
    FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Quay];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Quay') AND name = 'CI_quayPK')
    CREATE CLUSTERED INDEX [CI_quayPK] ON arc.[TTDB_Quay] ([quayPK]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Quay') AND name = 'NCI_quayNsr')
    CREATE NONCLUSTERED INDEX [NCI_quayNsr] ON arc.[TTDB_Quay] ([quayNsr], [dw_valid_from] DESC) INCLUDE ([stopId]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Quay') AND name = 'NCI_DateColumn')
    CREATE NONCLUSTERED INDEX [NCI_DateColumn] ON arc.[TTDB_Quay] ([created]);
GO

-------------------------------------------------------------------------------
-- arc.TTDB_Trip
-------------------------------------------------------------------------------
IF OBJECT_ID('arc.TTDB_Trip','U') IS NULL
BEGIN
    CREATE TABLE arc.[TTDB_Trip] (
        [tripPK] int NOT NULL,
        [importFileId] int NULL,
        [tripId] int NULL,
        [lineNr] varchar(50) NULL,
        [tripNr] int NULL,
        [departureTime] time(7) NULL,
        [offset] int NULL,
        [msm] int NULL,
        [outbound] bit NULL,
        [description] varchar(200) NULL,
        [tripBK] int NULL,
        [cs] int NULL,
        [created] date NULL,
        [InsertedDate_DW] datetime NULL,
        [UpdatedDate_DW] datetime NULL,
        CONSTRAINT [PK_TTDB_Trip] PRIMARY KEY CLUSTERED ([tripPK])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM arc.[TTDB_Trip])
BEGIN
    INSERT INTO arc.[TTDB_Trip] ([tripPK], [importFileId], [tripId], [lineNr], [tripNr], [departureTime], [offset], [msm], [outbound], [description], [tripBK], [cs], [created], [InsertedDate_DW], [UpdatedDate_DW])
    SELECT [tripPK], [importFileId], [tripId], [lineNr], [tripNr], [departureTime], [offset], [msm], [outbound], [description], [tripBK], [cs], [created], [InsertedDate_DW], [UpdatedDate_DW]
    FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Trip];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Trip') AND name = 'NCI_departureTime')
    CREATE NONCLUSTERED INDEX [NCI_departureTime] ON arc.[TTDB_Trip] ([departureTime]) INCLUDE ([offset]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Trip') AND name = 'NCI_DateColumn')
    CREATE NONCLUSTERED INDEX [NCI_DateColumn] ON arc.[TTDB_Trip] ([created]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Trip') AND name = 'NCI_TripId')
    CREATE NONCLUSTERED INDEX [NCI_TripId] ON arc.[TTDB_Trip] ([tripId]);
GO

-------------------------------------------------------------------------------
-- arc.TTDB_Passing
-------------------------------------------------------------------------------
IF OBJECT_ID('arc.TTDB_Passing','U') IS NULL
BEGIN
    CREATE TABLE arc.[TTDB_Passing] (
        [scheduleBK] int NOT NULL,
        [passingBK] int NOT NULL,
        [stopNr] int NULL,
        [deltaTime] int NULL,
        [waitTime] int NULL,
        [accTime] int NULL,
        [accWait] int NULL,
        [quayNsr] int NULL,
        [created] date NULL,
        [InsertedDate_DW] datetime NULL,
        [UpdatedDate_DW] datetime NULL,
        CONSTRAINT [PK_TTDB_Passing_1] PRIMARY KEY CLUSTERED ([passingBK], [scheduleBK])
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM arc.[TTDB_Passing])
BEGIN
    INSERT INTO arc.[TTDB_Passing] ([scheduleBK], [passingBK], [stopNr], [deltaTime], [waitTime], [accTime], [accWait], [quayNsr], [created], [InsertedDate_DW], [UpdatedDate_DW])
    SELECT [scheduleBK], [passingBK], [stopNr], [deltaTime], [waitTime], [accTime], [accWait], [quayNsr], [created], [InsertedDate_DW], [UpdatedDate_DW]
    FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Passing];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Passing') AND name = 'NCI_DateColumn')
    CREATE NONCLUSTERED INDEX [NCI_DateColumn] ON arc.[TTDB_Passing] ([created]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_Passing') AND name = 'NCI_ScheduleBK')
    CREATE NONCLUSTERED INDEX [NCI_ScheduleBK] ON arc.[TTDB_Passing] ([scheduleBK]) INCLUDE ([quayNsr]);
GO

-------------------------------------------------------------------------------
-- arc.TTDB_TripFacts
-------------------------------------------------------------------------------
IF OBJECT_ID('arc.TTDB_TripFacts','U') IS NULL
BEGIN
    CREATE TABLE arc.[TTDB_TripFacts] (
        [operatingDay] date NOT NULL,
        [tripPK] int NULL,
        [linePK] int NULL,
        [scheduleBK] int NULL,
        [routeBK] int NULL,
        [importFileId] int NOT NULL,
        [created] date NULL,
        [InsertedDate_DW] datetime NULL,
        [UpdatedDate_DW] datetime NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM arc.[TTDB_TripFacts])
BEGIN
    INSERT INTO arc.[TTDB_TripFacts] ([operatingDay], [tripPK], [linePK], [scheduleBK], [routeBK], [importFileId], [created], [InsertedDate_DW], [UpdatedDate_DW])
    SELECT [operatingDay], [tripPK], [linePK], [scheduleBK], [routeBK], [importFileId], [created], [InsertedDate_DW], [UpdatedDate_DW]
    FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_TripFacts];
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_TripFacts') AND name = 'NCI_KeyColumn')
    CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON arc.[TTDB_TripFacts] ([importFileId], [operatingDay]);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('arc.TTDB_TripFacts') AND name = 'NCI_IncrementalCol')
    CREATE NONCLUSTERED INDEX [NCI_IncrementalCol] ON arc.[TTDB_TripFacts] ([importFileId]);
GO

-------------------------------------------------------------------------------
-- Verification: row count and byte-sensitive checksum, new vs old
-------------------------------------------------------------------------------
SELECT 'TTDB_Lines' AS TableName,
       (SELECT COUNT_BIG(*) FROM arc.[TTDB_Lines]) AS NewRows,
       (SELECT COUNT_BIG(*) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Lines]) AS OldRows,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([Id], [Name], [TransportMode], [TransportSubMode], [PublicCode], [PrivateCode], [OperatorId], [GroupOfLinesId], [LineRef], [ImportFileId], [OperatorRef], [RepresentedByGroupRef], [NetworkId], [RegionCode], [PresentationColour], [PresentationTextColour], [InsertedDate_DW], [UpdatedDate_DW]))) FROM arc.[TTDB_Lines]) AS NewChecksum,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([Id], [Name], [TransportMode], [TransportSubMode], [PublicCode], [PrivateCode], [OperatorId], [GroupOfLinesId], [LineRef], [ImportFileId], [OperatorRef], [RepresentedByGroupRef], [NetworkId], [RegionCode], [PresentationColour], [PresentationTextColour], [InsertedDate_DW], [UpdatedDate_DW]))) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Lines]) AS OldChecksum;
GO
SELECT 'TTDB_Quays' AS TableName,
       (SELECT COUNT_BIG(*) FROM arc.[TTDB_Quays]) AS NewRows,
       (SELECT COUNT_BIG(*) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Quays]) AS OldRows,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([Id], [QuayRef], [Description], [PrivateCode], [Longitude], [Latitude], [ImportFileId], [NsrId], [KolumbusId], [PublicCode], [TransportMode], [RegionCode], [Discontinued], [Changed], [TransportSubmode], [InsertedDate_DW], [UpdatedDate_DW]))) FROM arc.[TTDB_Quays]) AS NewChecksum,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([Id], [QuayRef], [Description], [PrivateCode], [Longitude], [Latitude], [ImportFileId], [NsrId], [KolumbusId], [PublicCode], [TransportMode], [RegionCode], [Discontinued], [Changed], [TransportSubmode], [InsertedDate_DW], [UpdatedDate_DW]))) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Quays]) AS OldChecksum;
GO
SELECT 'TTDB_Quay' AS TableName,
       (SELECT COUNT_BIG(*) FROM arc.[TTDB_Quay]) AS NewRows,
       (SELECT COUNT_BIG(*) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Quay]) AS OldRows,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([quayPK], [quayNsr], [quayName], [lat], [lon], [stopPlaceNsr], [stopPlaceName], [stopId], [quayBK], [created], [updated_at], [cs], [dw_valid_from], [dw_valid_to], [InsertedDate_DW], [UpdatedDate_DW]))) FROM arc.[TTDB_Quay]) AS NewChecksum,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([quayPK], [quayNsr], [quayName], [lat], [lon], [stopPlaceNsr], [stopPlaceName], [stopId], [quayBK], [created], [updated_at], [cs], [dw_valid_from], [dw_valid_to], [InsertedDate_DW], [UpdatedDate_DW]))) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Quay]) AS OldChecksum;
GO
SELECT 'TTDB_Trip' AS TableName,
       (SELECT COUNT_BIG(*) FROM arc.[TTDB_Trip]) AS NewRows,
       (SELECT COUNT_BIG(*) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Trip]) AS OldRows,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([tripPK], [importFileId], [tripId], [lineNr], [tripNr], [departureTime], [offset], [msm], [outbound], [description], [tripBK], [cs], [created], [InsertedDate_DW], [UpdatedDate_DW]))) FROM arc.[TTDB_Trip]) AS NewChecksum,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([tripPK], [importFileId], [tripId], [lineNr], [tripNr], [departureTime], [offset], [msm], [outbound], [description], [tripBK], [cs], [created], [InsertedDate_DW], [UpdatedDate_DW]))) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Trip]) AS OldChecksum;
GO
SELECT 'TTDB_Passing' AS TableName,
       (SELECT COUNT_BIG(*) FROM arc.[TTDB_Passing]) AS NewRows,
       (SELECT COUNT_BIG(*) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Passing]) AS OldRows,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([scheduleBK], [passingBK], [stopNr], [deltaTime], [waitTime], [accTime], [accWait], [quayNsr], [created], [InsertedDate_DW], [UpdatedDate_DW]))) FROM arc.[TTDB_Passing]) AS NewChecksum,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([scheduleBK], [passingBK], [stopNr], [deltaTime], [waitTime], [accTime], [accWait], [quayNsr], [created], [InsertedDate_DW], [UpdatedDate_DW]))) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_Passing]) AS OldChecksum;
GO
SELECT 'TTDB_TripFacts' AS TableName,
       (SELECT COUNT_BIG(*) FROM arc.[TTDB_TripFacts]) AS NewRows,
       (SELECT COUNT_BIG(*) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_TripFacts]) AS OldRows,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([operatingDay], [tripPK], [linePK], [scheduleBK], [routeBK], [importFileId], [created], [InsertedDate_DW], [UpdatedDate_DW]))) FROM arc.[TTDB_TripFacts]) AS NewChecksum,
       (SELECT SUM(CONVERT(bigint, BINARY_CHECKSUM([operatingDay], [tripPK], [linePK], [scheduleBK], [routeBK], [importFileId], [created], [InsertedDate_DW], [UpdatedDate_DW]))) FROM [OLDPROD].[dw-dwh-prod].[arc].[TTDB_TripFacts]) AS OldChecksum;
GO

