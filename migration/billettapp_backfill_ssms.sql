/* ============================================================================================
   Billettapp: split the historical era from the live era and combine them in a view.

   Run in SSMS connected to:
       Server:   dw-mi-sql-prod.public.6b122fbc620a.database.windows.net,3342
       Database: dw-dwh-prod

   Run the whole file top to bottom, in ONE session. Four parts:

     PART 1  arc.Billettapp_Trans_hist  create, then copy old production's 8,141,727 rows
                                        over the OLDPROD linked server. Loaded once, then frozen.
     PART 2  arc.Billettapp_Trans_live  create, empty. This is where the V3 flow writes.
     PART 3  arc.Billettapp_Trans       the consumer-facing VIEW over both eras.
     PART 4  verification.

   The end state:

       arc.Billettapp_Trans        VIEW    what every downstream consumer queries. Old
                                           production's exact column set, order, names and types.
       arc.Billettapp_Trans_live   TABLE   the V3 ingestion's target. 2026-07-29 onward plus
                                           whatever the api flow re-fetches for earlier dates.
       arc.Billettapp_Trans_hist   TABLE   old production's accumulated history, 2024-02 onward.

   Both tables carry old production's EXACT shape (generated from old prod's own sys.columns in
   column_id order), so the view is a straight UNION ALL of two positionally aligned column lists
   and needs no per-column CAST to hold the contract:
     - [vatAmount] and [passenger_vatAmount] keep old production's numeric(7,2). The V3 engine
       normalises numeric to decimal when it plans schema changes, so this matches old production
       byte for byte without provoking an ALTER on every run.
     - [id] keeps old production's NOT NULL. The engine never tightens or loosens an existing
       column's nullability, it only reports drift, so this is stable across runs.

   Overlap handling: the two eras will both hold the boundary days (old production ran through
   2026-07-28, and the V3 api flow re-fetches from 2026-07-19 so nothing it captured is lost).
   Rather than pick an arbitrary cutover date, the view takes every live row plus only those
   historical rows whose [id] is absent from live. That is exact in both directions: no order is
   counted twice, and none is dropped because a boundary guess was off by a day. Where an order
   exists in both eras the live row wins, which is correct since it is the fresher capture.

   Surrogate keys: history keeps old production's BillettappTransPK verbatim (1 .. 8,141,727) so
   the values downstream may have seen stay stable. The live table therefore starts its identity
   at 20,000,000, well clear of history, so BillettappTransPK is unique across the view and the
   era a row came from is obvious from its key.

   PART 2 IS DESTRUCTIVE: it drops the current arc.Billettapp_Trans table, which holds 54,480 rows
   from the 2026-07-22 smoke test (orderDate 2026-07-19 to 2026-07-24). Every one of those dates is
   inside the window the api flow re-fetches, and the name is needed for the view.

   Why the linked server rather than replaying the API for history: old production's arc table is
   the ACCUMULATED record of what the legacy function actually captured since 2024-02, including
   orders whose export changed after the function's 3-day window had passed, and the five onboard*
   columns the current API response no longer carries. Old production is an Azure SQL Database and
   the new estate a Managed Instance, so there is no native restore path between them, and a
   client-side bulk copy bottlenecks on the operator's local link. This runs over Azure's backbone.
   Same approach as trapeze_backfill_linked.sql and fara_transfer_linked.sql.

   PART 1 starts from TRUNCATE, so it is re-runnable as a whole. Do not resume a partial run by
   editing the starting value.
   ============================================================================================ */

SET NOCOUNT ON;
GO

/* ======================= PART 1: history table, loaded from old prod ======================= */

IF OBJECT_ID('arc.Billettapp_Trans_hist', 'U') IS NOT NULL
    DROP TABLE arc.Billettapp_Trans_hist;
GO

CREATE TABLE arc.Billettapp_Trans_hist
(
    [BillettappTransPK] int NOT NULL,          /* copied verbatim from old prod, not an identity */
    [passenger_id] varchar(50) NULL,
    [toStop] varchar(50) NULL,
    [creditAmount] smallint NULL,
    [csOrderedBy] varchar(50) NULL,
    [passenger_vatAmount] numeric(7,2) NULL,
    [ticketStatus] varchar(50) NULL,
    [creditDate] varchar(50) NULL,
    [appInstanceName] varchar(50) NULL,
    [toZone] varchar(50) NULL,
    [appInstanceId] varchar(50) NULL,
    [ticketType] varchar(50) NULL,
    [passenger_amount] smallint NULL,
    [fromStop] varchar(50) NULL,
    [paymentId] varchar(50) NULL,
    [validTo] datetime NULL,
    [fromZone] varchar(50) NULL,
    [passenger_count] smallint NULL,
    [csInvoiceReference] varchar(50) NULL,
    [payerAppVersion] date NULL,
    [nrOfZones] smallint NULL,
    [paymentStatus] varchar(50) NULL,
    [orderStatusDate] varchar(50) NULL,
    [paymentMethod] varchar(50) NULL,
    [payerAppPlatform] varchar(50) NULL,
    [payerTelephoneType] varchar(50) NULL,
    [vatAmount] numeric(7,2) NULL,
    [productTemplateId] smallint NULL,
    [owner] smallint NULL,
    [companyAgreementRef] varchar(50) NULL,
    [payerId] varchar(50) NULL,
    [payerOsVersion] date NULL,
    [vatPercentage] smallint NULL,
    [distributionType] varchar(50) NULL,
    [csComment] varchar(255) NULL,
    [orderStatus] varchar(50) NULL,
    [transType] varchar(50) NULL,
    [orderId] varchar(50) NULL,
    [passenger_productId] smallint NULL,
    [amount] smallint NULL,
    [passenger_profileId] smallint NULL,
    [passenger_profile] varchar(50) NULL,
    [id] varchar(50) NOT NULL,
    [allZones] varchar(50) NULL,
    [ticketNumber] int NULL,
    [validFrom] datetime NULL,
    [orderDate] datetime NULL,
    [payerAppInstanceName] varchar(50) NULL,
    [passenger_vatPercentage] smallint NULL,
    [FileDate_DW] decimal(14,0) NULL,
    [FileName_DW] varchar(255) NULL,
    [FileRowDate_DW] datetime NULL,
    [FileSize_DW] decimal(18,0) NULL,
    [DataSet_DW] decimal(14,0) NULL,
    [InsertedDate_DW] datetime NULL,
    [UpdatedDate_DW] datetime NULL,
    [onboardLineId] varchar(50) NULL,
    [onboardKbNr] varchar(50) NULL,
    [onboardDriverId] varchar(50) NULL,
    [onboardChainId] varchar(50) NULL,
    [onboardVin] varchar(50) NULL,
    [channel] varchar(50) NULL,
    [deliveryType] varchar(50) NULL,
    [bedrift] varchar(50) NULL,
    [companyName] varchar(50) NULL,
    [travelCardNumber] varchar(50) NULL,
    CONSTRAINT PK_Billettapp_Trans_hist PRIMARY KEY CLUSTERED ([BillettappTransPK])
);
GO

/* [id] carries the view's anti-join, so it is indexed rather than merely unique-by-accident. */
CREATE UNIQUE NONCLUSTERED INDEX NCI_KeyColumn ON arc.Billettapp_Trans_hist ([id]);
CREATE NONCLUSTERED INDEX NCI_IncrementalCol ON arc.Billettapp_Trans_hist ([FileDate_DW]);
GO

/* InsertedDate_DW and UpdatedDate_DW copy across as ordinary data, so the audit history is
   preserved, and FileName_DW keeps pointing at the legacy billettapp_YYYY-MM-DD.csv capture that
   produced each row. Rows the V3 flow lands carry billettapp_YYYY-MM-DD.json names in the live
   table, so the two eras stay distinguishable through the view. */

PRINT '>>> START Billettapp_Trans_hist';
TRUNCATE TABLE arc.Billettapp_Trans_hist;

DECLARE @lo bigint, @max bigint, @moved bigint = 0, @m varchar(200);
SELECT @lo = MIN([BillettappTransPK]), @max = MAX([BillettappTransPK])
FROM [OLDPROD].[dw-dwh-prod].[arc].[Billettapp_Trans];

WHILE @lo <= @max
BEGIN
    INSERT INTO arc.Billettapp_Trans_hist WITH (TABLOCK)
        ([BillettappTransPK],
         [passenger_id],[toStop],[creditAmount],[csOrderedBy],[passenger_vatAmount],[ticketStatus],[creditDate],
         [appInstanceName],[toZone],[appInstanceId],[ticketType],[passenger_amount],[fromStop],[paymentId],[validTo],
         [fromZone],[passenger_count],[csInvoiceReference],[payerAppVersion],[nrOfZones],[paymentStatus],[orderStatusDate],
         [paymentMethod],[payerAppPlatform],[payerTelephoneType],[vatAmount],[productTemplateId],[owner],
         [companyAgreementRef],[payerId],[payerOsVersion],[vatPercentage],[distributionType],[csComment],[orderStatus],
         [transType],[orderId],[passenger_productId],[amount],[passenger_profileId],[passenger_profile],[id],[allZones],
         [ticketNumber],[validFrom],[orderDate],[payerAppInstanceName],[passenger_vatPercentage],[FileDate_DW],[FileName_DW],
         [FileRowDate_DW],[FileSize_DW],[DataSet_DW],[InsertedDate_DW],[UpdatedDate_DW],[onboardLineId],[onboardKbNr],
         [onboardDriverId],[onboardChainId],[onboardVin],[channel],[deliveryType],[bedrift],[companyName],[travelCardNumber])
    SELECT
         [BillettappTransPK],
         [passenger_id],[toStop],[creditAmount],[csOrderedBy],[passenger_vatAmount],[ticketStatus],[creditDate],
         [appInstanceName],[toZone],[appInstanceId],[ticketType],[passenger_amount],[fromStop],[paymentId],[validTo],
         [fromZone],[passenger_count],[csInvoiceReference],[payerAppVersion],[nrOfZones],[paymentStatus],[orderStatusDate],
         [paymentMethod],[payerAppPlatform],[payerTelephoneType],[vatAmount],[productTemplateId],[owner],
         [companyAgreementRef],[payerId],[payerOsVersion],[vatPercentage],[distributionType],[csComment],[orderStatus],
         [transType],[orderId],[passenger_productId],[amount],[passenger_profileId],[passenger_profile],[id],[allZones],
         [ticketNumber],[validFrom],[orderDate],[payerAppInstanceName],[passenger_vatPercentage],[FileDate_DW],[FileName_DW],
         [FileRowDate_DW],[FileSize_DW],[DataSet_DW],[InsertedDate_DW],[UpdatedDate_DW],[onboardLineId],[onboardKbNr],
         [onboardDriverId],[onboardChainId],[onboardVin],[channel],[deliveryType],[bedrift],[companyName],[travelCardNumber]
    FROM [OLDPROD].[dw-dwh-prod].[arc].[Billettapp_Trans]
    WHERE [BillettappTransPK] BETWEEN @lo AND @lo + 999999;

    SET @moved += @@ROWCOUNT;
    SET @m = '    Billettapp_Trans_hist: ' + CAST(@moved AS varchar(20))
           + ' rows (through BillettappTransPK ' + CAST(@lo + 999999 AS varchar(20)) + ')';
    RAISERROR(@m, 0, 1) WITH NOWAIT;
    SET @lo += 1000000;
END

SET @m = '>>> DONE Billettapp_Trans_hist: ' + CAST(@moved AS varchar(20)) + ' rows';
RAISERROR(@m, 0, 1) WITH NOWAIT;
GO

/* ============================ PART 2: live table for the V3 flow ============================ */

/* Drops the smoke-test table so the name is free for the view. */
IF OBJECT_ID('arc.Billettapp_Trans', 'V') IS NOT NULL
    DROP VIEW arc.Billettapp_Trans;
IF OBJECT_ID('arc.Billettapp_Trans', 'U') IS NOT NULL
    DROP TABLE arc.Billettapp_Trans;
GO

IF OBJECT_ID('arc.Billettapp_Trans_live', 'U') IS NOT NULL
    DROP TABLE arc.Billettapp_Trans_live;
GO

CREATE TABLE arc.Billettapp_Trans_live
(
    /* starts clear of history's 1 .. 8,141,727 so the surrogate is unique across the view */
    [BillettappTransPK] int IDENTITY(20000000,1) NOT NULL,
    [passenger_id] varchar(50) NULL,
    [toStop] varchar(50) NULL,
    [creditAmount] smallint NULL,
    [csOrderedBy] varchar(50) NULL,
    [passenger_vatAmount] numeric(7,2) NULL,
    [ticketStatus] varchar(50) NULL,
    [creditDate] varchar(50) NULL,
    [appInstanceName] varchar(50) NULL,
    [toZone] varchar(50) NULL,
    [appInstanceId] varchar(50) NULL,
    [ticketType] varchar(50) NULL,
    [passenger_amount] smallint NULL,
    [fromStop] varchar(50) NULL,
    [paymentId] varchar(50) NULL,
    [validTo] datetime NULL,
    [fromZone] varchar(50) NULL,
    [passenger_count] smallint NULL,
    [csInvoiceReference] varchar(50) NULL,
    [payerAppVersion] date NULL,
    [nrOfZones] smallint NULL,
    [paymentStatus] varchar(50) NULL,
    [orderStatusDate] varchar(50) NULL,
    [paymentMethod] varchar(50) NULL,
    [payerAppPlatform] varchar(50) NULL,
    [payerTelephoneType] varchar(50) NULL,
    [vatAmount] numeric(7,2) NULL,
    [productTemplateId] smallint NULL,
    [owner] smallint NULL,
    [companyAgreementRef] varchar(50) NULL,
    [payerId] varchar(50) NULL,
    [payerOsVersion] date NULL,
    [vatPercentage] smallint NULL,
    [distributionType] varchar(50) NULL,
    [csComment] varchar(255) NULL,
    [orderStatus] varchar(50) NULL,
    [transType] varchar(50) NULL,
    [orderId] varchar(50) NULL,
    [passenger_productId] smallint NULL,
    [amount] smallint NULL,
    [passenger_profileId] smallint NULL,
    [passenger_profile] varchar(50) NULL,
    [id] varchar(50) NOT NULL,
    [allZones] varchar(50) NULL,
    [ticketNumber] int NULL,
    [validFrom] datetime NULL,
    [orderDate] datetime NULL,
    [payerAppInstanceName] varchar(50) NULL,
    [passenger_vatPercentage] smallint NULL,
    [FileDate_DW] decimal(14,0) NULL,
    [FileName_DW] varchar(255) NULL,
    [FileRowDate_DW] datetime NULL,
    [FileSize_DW] decimal(18,0) NULL,
    [DataSet_DW] decimal(14,0) NULL,
    [InsertedDate_DW] datetime NULL,
    [UpdatedDate_DW] datetime NULL,
    [onboardLineId] varchar(50) NULL,
    [onboardKbNr] varchar(50) NULL,
    [onboardDriverId] varchar(50) NULL,
    [onboardChainId] varchar(50) NULL,
    [onboardVin] varchar(50) NULL,
    [channel] varchar(50) NULL,
    [deliveryType] varchar(50) NULL,
    [bedrift] varchar(50) NULL,
    [companyName] varchar(50) NULL,
    [travelCardNumber] varchar(50) NULL,
    CONSTRAINT PK_Billettapp_Trans_live PRIMARY KEY CLUSTERED ([BillettappTransPK])
);
GO

/* NCI_KeyColumn is the flow's merge key and the view's anti-join probe; NCI_IncrementalCol is the
   FileDate_DW watermark the ods flow reads. Same index set the V3 ingestion builds itself. */
CREATE UNIQUE NONCLUSTERED INDEX NCI_KeyColumn ON arc.Billettapp_Trans_live ([id]);
CREATE NONCLUSTERED INDEX NCI_IncrementalCol ON arc.Billettapp_Trans_live ([FileDate_DW]);
CREATE NONCLUSTERED INDEX NCI_UpdatedDate_DW ON arc.Billettapp_Trans_live ([UpdatedDate_DW]);
GO

/* ============================== PART 3: consumer-facing view ============================== */

/* Old production's exact column set, order, names and types. Both underlying tables were created
   from old prod's own definition, so listing the columns in order is enough to hold the contract;
   no per-column CAST is needed and none is used, which keeps the view sargable. */
CREATE OR ALTER VIEW arc.Billettapp_Trans
AS
SELECT
    [BillettappTransPK],[passenger_id],[toStop],[creditAmount],[csOrderedBy],[passenger_vatAmount],
    [ticketStatus],[creditDate],[appInstanceName],[toZone],[appInstanceId],[ticketType],[passenger_amount],
    [fromStop],[paymentId],[validTo],[fromZone],[passenger_count],[csInvoiceReference],[payerAppVersion],
    [nrOfZones],[paymentStatus],[orderStatusDate],[paymentMethod],[payerAppPlatform],[payerTelephoneType],
    [vatAmount],[productTemplateId],[owner],[companyAgreementRef],[payerId],[payerOsVersion],[vatPercentage],
    [distributionType],[csComment],[orderStatus],[transType],[orderId],[passenger_productId],[amount],
    [passenger_profileId],[passenger_profile],[id],[allZones],[ticketNumber],[validFrom],[orderDate],
    [payerAppInstanceName],[passenger_vatPercentage],[FileDate_DW],[FileName_DW],[FileRowDate_DW],
    [FileSize_DW],[DataSet_DW],[InsertedDate_DW],[UpdatedDate_DW],[onboardLineId],[onboardKbNr],
    [onboardDriverId],[onboardChainId],[onboardVin],[channel],[deliveryType],[bedrift],[companyName],
    [travelCardNumber]
FROM arc.Billettapp_Trans_live
UNION ALL
SELECT
    h.[BillettappTransPK],h.[passenger_id],h.[toStop],h.[creditAmount],h.[csOrderedBy],h.[passenger_vatAmount],
    h.[ticketStatus],h.[creditDate],h.[appInstanceName],h.[toZone],h.[appInstanceId],h.[ticketType],h.[passenger_amount],
    h.[fromStop],h.[paymentId],h.[validTo],h.[fromZone],h.[passenger_count],h.[csInvoiceReference],h.[payerAppVersion],
    h.[nrOfZones],h.[paymentStatus],h.[orderStatusDate],h.[paymentMethod],h.[payerAppPlatform],h.[payerTelephoneType],
    h.[vatAmount],h.[productTemplateId],h.[owner],h.[companyAgreementRef],h.[payerId],h.[payerOsVersion],h.[vatPercentage],
    h.[distributionType],h.[csComment],h.[orderStatus],h.[transType],h.[orderId],h.[passenger_productId],h.[amount],
    h.[passenger_profileId],h.[passenger_profile],h.[id],h.[allZones],h.[ticketNumber],h.[validFrom],h.[orderDate],
    h.[payerAppInstanceName],h.[passenger_vatPercentage],h.[FileDate_DW],h.[FileName_DW],h.[FileRowDate_DW],
    h.[FileSize_DW],h.[DataSet_DW],h.[InsertedDate_DW],h.[UpdatedDate_DW],h.[onboardLineId],h.[onboardKbNr],
    h.[onboardDriverId],h.[onboardChainId],h.[onboardVin],h.[channel],h.[deliveryType],h.[bedrift],h.[companyName],
    h.[travelCardNumber]
FROM arc.Billettapp_Trans_hist h
WHERE NOT EXISTS (SELECT 1 FROM arc.Billettapp_Trans_live l WHERE l.[id] = h.[id]);
GO

/* =================================== PART 4: verification =================================== */

/* 1. History copied completely. Both rows should agree on count, distinct [id], and date range.
      Expected (old prod measured 2026-07-30): 8,141,727 rows and as many distinct ids, orderDate
      2024-02-08 onward. */
SELECT 'hist (new)' AS Side, COUNT_BIG(*) AS Rws, COUNT(DISTINCT [id]) AS Ids,
       MIN(orderDate) AS MinOrderDate, MAX(orderDate) AS MaxOrderDate
FROM arc.Billettapp_Trans_hist
UNION ALL
SELECT 'old prod', Rws, Ids, MinOrderDate, MaxOrderDate
FROM OPENQUERY(OLDPROD, 'SELECT COUNT_BIG(*) AS Rws, COUNT(DISTINCT [id]) AS Ids, MIN(orderDate) AS MinOrderDate, MAX(orderDate) AS MaxOrderDate FROM arc.Billettapp_Trans');
GO

/* 2. The view reconciles: total equals live plus the non-overlapping history, and every [id] in it
      is distinct. Before the api flow runs, live is empty and the view equals history exactly. */
SELECT (SELECT COUNT_BIG(*) FROM arc.Billettapp_Trans_live) AS LiveRows,
       (SELECT COUNT_BIG(*) FROM arc.Billettapp_Trans_hist) AS HistRows,
       (SELECT COUNT_BIG(*) FROM arc.Billettapp_Trans)      AS ViewRows,
       (SELECT COUNT_BIG(DISTINCT [id]) FROM arc.Billettapp_Trans) AS ViewDistinctIds;
GO

/* 3. Shape check: the VIEW must match old production's table column for column, in order. This
      must return ZERO rows. numeric and decimal are treated as equal, since they are the same
      storage type and the V3 engine normalises between them. */
WITH nw AS (
    SELECT c.column_id, c.name, t.name AS typ, c.max_length, c.precision, c.scale
    FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID('arc.Billettapp_Trans')
), op AS (
    SELECT * FROM OPENQUERY(OLDPROD,
      'SELECT c.column_id, c.name, t.name AS typ, c.max_length, c.precision, c.scale
       FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
       WHERE c.object_id = OBJECT_ID(''arc.Billettapp_Trans'')')
)
SELECT COALESCE(nw.column_id, op.column_id) AS Position,
       nw.name AS NewName, nw.typ AS NewType, op.name AS OldName, op.typ AS OldType
FROM nw FULL JOIN op ON op.column_id = nw.column_id
WHERE nw.name IS NULL OR op.name IS NULL
   OR nw.name <> op.name
   OR nw.max_length <> op.max_length OR nw.precision <> op.precision OR nw.scale <> op.scale
   OR (nw.typ <> op.typ AND NOT (nw.typ IN ('numeric','decimal') AND op.typ IN ('numeric','decimal')))
ORDER BY Position;
GO
