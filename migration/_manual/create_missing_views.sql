/*
  Recreates in the new prod DWH the views that exist in OLD production but were
  missing after the migration, using old production's own definitions.

  The stored definition of edw.V_MpcTripSummary still carries its pre-rename name
  (V_test_MpcTripSummary) because sp_rename does not rewrite sys.sql_modules, so the
  CREATE header is rewritten here to the view's real name.

  Deliberately NOT included: arc.V_Fact_TTDB_Passing. It is already broken in OLD
  production, selecting tripFactsPK from arc.TTDB_TripFacts, a column that no longer
  exists on that table. It cannot bind here either, and porting it verbatim would only
  reproduce the fault. It needs a rewrite by whoever owns the TTDB fact model.
*/

-------------------------------------------------------------------------------
-- arc.V_APC_MatchedTrip
-------------------------------------------------------------------------------
CREATE OR ALTER VIEW [arc].[V_APC_MatchedTrip]
AS
SELECT c.[CallsPK],
       c.[OperatingCalendarDay] AS [CalendarID],
       c.[SourceSystemID] AS [SourceSystemID],
       c.[Id] AS [UniqueId],
       [Line_DW],
       pj.[JourneyName] AS [TripNo],
       CASE
           WHEN ISNUMERIC(pj.[JourneyName]) = 1
                AND ISNUMERIC([LineId_DW]) = 1 THEN
               CAST([LineId_DW] AS VARCHAR(25)) + CAST(pj.[JourneyName] AS VARCHAR(25))
           ELSE
               NULL
       END [TripId],
       c.[OperatingCalendarDay] AS [OperatingDate],
       [VehicleNo_DW],
       [SequenceInJourney] AS [StopSeq],
       [StopNo_DW],
       sp.[Name] AS [StopName],
       sp.[Latitude],
       sp.[Longitude],
       [ArrivalPlan_DW],
       [DeparturePlan_DW],
       [ArrivalReal_DW],
       [DepartureReal_DW],
       ISNULL([totalIn], 0) AS [Boardings],
       ISNULL([totalOut], 0) AS [Alightings],
       [PassengersOnboard] AS [LoadDeparting],
       CAST(ISNULL(ISNULL([MeasuredDistanceToNextPointInJourney], 0) * ISNULL([PassengersOnboard], 0), 0) AS VARCHAR(255)) AS [PassengerKmDeparting],
       CONCAT(c.[OperatingCalendarDay], pj.[JourneyName]) AS DatoTripNo,
       CONCAT([SequenceInJourney], ' - ', sp.[Name]) AS StopSeqStopName,
       CAST([JourneyStartTime] AS TIME) AS [TripStartTime],
       RIGHT('0' + CAST(DATEPART(HH, [JourneyStartTime]) AS VARCHAR(2)), 2) AS [TripStartHH],
       --,[UsesStopPointId] AS [StopNo]

       RIGHT('0' + CAST(DATEPART(HH, [JourneyStartTime]) AS VARCHAR(2)), 2) + ':XX' AS [TripStartHHXX],
       CONCAT(ISNULL(NULLIF(pj.[JourneyName], '***'), 0000), ' (', [JourneyStartTime], ')') AS [TripAvgTid],
       CONCAT([JourneyStartTime], pj.[JourneyName]) AS [StartTidTripNoSort],
       CAST([PassengersOnboard] + [totalIn] - [totalOut] AS DECIMAL) AS [Load],
       [LineId_DW] AS LinjeNr,
       RIGHT('0' + CAST(DATEPART(HH, [DepartureReal_DW]) AS VARCHAR(2)), 2) AS "Avgang-HH",
       [LineId_DW] AS LinjeNavn,
       CAST(SUBSTRING(ISNULL(NULLIF(pj.[JourneyName], '***'), 0000), 1, 1) AS INT) AS Retning,
       CASE
           WHEN (ISNULL([PassengersOnboard], 0) = 0) THEN
               0
           ELSE
               CAST(ISNULL(
                              ISNULL(
                                        ISNULL(
                                                  ISNULL([MeasuredDistanceToNextPointInJourney], 0)
                                                  * ISNULL([PassengersOnboard], 0),
                                                  0
                                              ),
                                        0
                                    ) / CAST([PassengersOnboard] AS INT),
                              0
                          ) AS DECIMAL(16, 3))
       END AS Avstand,
       CASE
           WHEN (CAST(SUBSTRING(ISNULL(NULLIF(pj.[JourneyName], '***'), 0000), 1, 1) AS INT) % 2 = 0) THEN
               'Sekundær'
           ELSE
               'Primær'
       END AS RetningFlagg,
       CONCAT(
                 [Line_DW],
                 '   [',
                 CONCAT(ISNULL(NULLIF(pj.[JourneyName], '***'), 0000), ' (', CAST([JourneyStartTime] AS TIME), ')]')
             ) AS Rute,
       (
           SELECT MAX(v)
           FROM
           (
               VALUES
                   (c.[UpdatedDate_DW]),
                   (pc.[UpdatedDate_DW]),
                   (pj.[UpdatedDate_DW]),
                   (l.[UpdatedDate_DW]),
                   (sp.[UpdatedDate_DW]),
                   (pb.[UpdatedDate_DW])
           ) AS value (v)
       ) AS [UpdatedDate_DW]
	   --c.[UpdatedDate_DW],
       ,pb.BlockName
	   ,sp.[Region]
	   ,sp.[Kommune]
--into #temp2
FROM [arc].[APC_Calls] c
    LEFT JOIN [arc].[APC_PassengerCount] pc
        ON pc.[SourceSystemID] = c.[SourceSystemID]
           AND pc.[HappensAtCallId] = c.[Id]
           AND pc.[OperatingCalendarDay] = c.[OperatingCalendarDay]
    LEFT JOIN [arc].[APC_PlannedJourneys] pj --WITH (INDEX = [NCI_KeyColumn])
        ON pj.[SourceSystemID] = c.[SourceSystemID]
           AND pj.[Id] = c.[UsesPlannedJourneyId]
           AND pj.[OperatingCalendarDay] = c.[OperatingCalendarDay]
    LEFT JOIN [arc].[APC_Line] l
        ON pj.[SourceSystemID] = l.[SourceSystemID]
           AND pj.[BelongsToLineId] = l.[Id]
           AND pj.[OperatingCalendarDay] = l.[OperatingCalendarDay]
    LEFT JOIN [arc].[APC_StopPoint] sp --WITH (INDEX = [test])
        ON sp.[SourceSystemID] = c.[SourceSystemID]
           AND sp.[OperatingCalendarDay] = c.[OperatingCalendarDay]
           AND sp.[Id] = c.[UsesStopPointId]
    LEFT JOIN [arc].[APC_PlannedBlocks] pb
        ON pb.[SourceSystemID] = c.[SourceSystemID]
           AND pb.[Id] = c.[UsesPlannedBlockId]
           AND pb.[OperatingCalendarDay] = c.[OperatingCalendarDay]
WHERE c.[IsValid] = 1
--AND c.[OperatingCalendarDay] >= '2022-11-19'
--AND c.[OperatingCalendarDay] <= '2022-11-22'
--AND c.[SourceSystemID] = 37
	
-- AND      c.[UpdatedDate_DW] > DATEADD(dd, -1, GETDATE());
--[edw].[Post_APC_MatchedTrip]
GO

-------------------------------------------------------------------------------
-- arc.V_Mobilapp_Fact_salg_merged
-------------------------------------------------------------------------------
CREATE OR ALTER VIEW [arc].[V_Mobilapp_Fact_salg_merged]
AS
SELECT
	[MobilAppSalgPK] AS [MobilAppSalgPK]
    ,[Betalingsstatus] AS [Betalingsstatus]
	,[Billettype_navn] AS [Billettype_navn]
    ,[Bruker_ID] AS [Bruker_ID]

    ,[Fra_Sone_ID] AS [Fra_Sone_ID]
    ,[Fra_sone_navn] AS [Fra_sone_navn]
    ,[Fra_stopp_ID] AS [Fra_stopp_ID]
    ,[Fra_stopp_Navn] AS [Fra_stopp_Navn]

    ,[Til_sone_ID] AS [Til_sone_ID]
    ,[Til_sone_navn] AS [Til_sone_navn]
    ,[Til_stopp_ID] AS [Til_stopp_ID]
    ,[Til_stopp_navn] AS [Til_stopp_navn]

    ,[Antall_Soner]
	,NULL AS [allZones]

    ,[Beløp]
    ,[vatamount]
	, NULL as [vatPercentage]
	,[Belop_eks_mva]

	,[Antall]
	
	,[Koblet_til_bedrift_ID]
	,[koblet_til_bedrift_navn]
    ,[Antall_inspeksjoner]
    ,[Ant_HJH_Billett_Ord_DW]
    ,[Ant_HJH_Billett_Kamp_DW]
    ,[Ant_Billett_knyttet_til_Bedrift_DW]
    ,[Ant_30dager_ovrige_kunder_DW]
    ,[Ant_EnkeltBilletter_knyttet_til_Bedrift_DW]
    ,[Ant_HJH_FoersteKjoep_DW]
    ,[Ant_HJH_Gjenkjoep_DW]

    ,[PeriodID]
    ,[SourceSystemID]
    ,[Billett_ID]
    ,[Billett_element_ID]
    ,[Billettype_ID]

	,[Billettkanal_ID]
    ,[Billettkanal_navn]
    ,[Betalingskanal_ID]
    ,[Betalingskanal_navn]
    ,[Foreldrebruker_ID]
    ,[Betalt_av_bedrift_ID]

    ,[Kjopstidspunkt]
    ,[Salgsdato]
    
    ,[Rabattprosent]
    ,[Rabatt]
    
    ,[Enhetstype]
    ,[Refundert]
    ,[Refundert_belop]

	,NULL AS [creditAmount]
    ,NULL AS [creditDate]

	,NULL AS [csComment]
    ,NULL AS [csInvoiceReference]
    ,NULL AS [csOrderedBy]

	,NULL AS [appInstanceName]
    ,NULL AS [id]
    ,CAST(Salgsdato AS datetime) + CAST(Kjopstidspunkt AS datetime) AS [orderDate]
    ,NULL AS [orderId]
    ,NULL AS [orderStatusDate]
    ,NULL AS [owner]
    ,NULL AS [passenger_amount]
    ,NULL AS [passenger_id]
    ,NULL AS [distributionType]

    ,NULL AS [passenger_profileId]
    ,NULL AS [passenger_vatAmount]
    ,NULL AS [passenger_vatPercentage]

	,NULL AS [payerAppInstanceName]
    ,NULL AS [payerAppPlatform]
    ,NULL AS [payerAppVersion]
    ,NULL AS [payerId]
    ,NULL AS [payerOsVersion]
    ,NULL AS [payerTelephoneType]

	,NULL AS [paymentId]
    ,NULL AS [paymentMethod]
    ,NULL AS [paymentStatus]
    ,NULL AS [productTemplateId]
    ,NULL AS [ticketNumber]
    ,NULL AS [ticketStatus]
    ,NULL AS [ticketType]
    ,NULL AS [transType]

	,NULL AS [validFrom]
    ,NULL AS [validTo]

FROM
    [arc].[V_Mobilapp_Fact_salg]
UNION ALL
SELECT
	NULL AS [MobilAppSalgPK]
    ,[orderStatus] AS [Betalingsstatus]
	,[passenger_profile] AS [Billettype_navn]
	,[appInstanceId] AS [Bruker_ID]

	,NULL AS [Fra_Sone_ID]
    ,[fromZone] AS [Fra_sone_navn]
    ,NULL AS [Fra_stopp_ID]
    ,[fromStop] AS [Fra_stopp_Navn]

    , NULL AS [Til_sone_ID]
    ,[toZone] AS [Til_sone_navn]
	,NULL AS [Til_stopp_ID]
    ,[toStop] AS Til_stopp_navn

    ,[nrOfZones] AS [Antall_Soner]
	,[allZones] AS [allZones]
    
    ,CAST([amount] AS numeric(10, 0)) AS [Beløp]
	,[vatAmount] AS [vatamount]
	,[vatPercentage]
	, NULL AS [Belop_eks_mva]

	,[passenger_count] AS [Antall]     

    ,[companyAgreementRef] AS [Koblet_til_bedrift_ID]
	, NULL AS [koblet_til_bedrift_navn]
    , NULL AS [Antall_inspeksjoner]
    , NULL AS [Ant_HJH_Billett_Ord_DW]
    , NULL AS [Ant_HJH_Billett_Kamp_DW]
    , NULL AS [Ant_Billett_knyttet_til_Bedrift_DW]
    , NULL AS [Ant_30dager_ovrige_kunder_DW]
    , NULL AS [Ant_EnkeltBilletter_knyttet_til_Bedrift_DW]
    , NULL AS [Ant_HJH_FoersteKjoep_DW]
    , NULL AS [Ant_HJH_Gjenkjoep_DW]

	, NULL AS [PeriodID]
    --, NULL AS [BillettypeID]
    , NULL AS [SourceSystemID]
    , NULL AS [Billett_ID]
    , NULL AS [Billett_element_ID]
	,[passenger_productId] AS [Billettype_ID]

	, NULL AS [Billettkanal_ID]
    , NULL AS [Billettkanal_navn]
    , NULL AS [Betalingskanal_ID]
    , NULL AS [Betalingskanal_navn]
    , NULL AS [Foreldrebruker_ID]
    , NULL AS [Betalt_av_bedrift_ID]

	,NULL AS [Kjopstidspunkt]
    ,NULL AS [Salgsdato]

	, NULL AS [Rabattprosent]
    , NULL AS [Rabatt]

	, NULL AS [Enhetstype]
    , NULL AS [Refundert]
    , NULL AS [Refundert_belop]

	,[creditAmount]
    ,[creditDate]

    ,[csComment]
    ,[csInvoiceReference]
    ,[csOrderedBy]

     ,[appInstanceName]
     ,[id]
     ,[orderDate]
     ,[orderId]
     ,[orderStatusDate]
     ,[owner]
     ,[passenger_amount]
     ,[passenger_id]
     ,[distributionType]
    
    ,[passenger_profileId]
    ,[passenger_vatAmount]
    ,[passenger_vatPercentage]

    ,[payerAppInstanceName]
    ,[payerAppPlatform]
    ,[payerAppVersion]
    ,[payerId]
    ,[payerOsVersion]
    ,[payerTelephoneType]

    ,[paymentId]
    ,[paymentMethod]
    ,[paymentStatus]
    ,[productTemplateId]
    ,[ticketNumber]
    ,[ticketStatus]
    ,[ticketType]
    ,[transType]

    ,[validFrom]
    ,[validTo]
FROM
    [arc].[Billettapp_Trans]
GO

-------------------------------------------------------------------------------
-- edw.V_MpcTripSummary
-------------------------------------------------------------------------------
CREATE OR ALTER VIEW [edw].[V_MpcTripSummary]
AS
SELECT
       [edw].[MPC_MpcTripSummary].[MpcTripId]
      ,[edw].[MPC_MpcTripSummary].[TripId]
      ,[edw].[MPC_MpcTripSummary].[TripStatus]
      ,[edw].[MPC_MpcTripSummary].[TripLastModified]
      ,[edw].[MPC_MpcTripSummary].[StopName]
      ,[edw].[MPC_MpcTripSummary].[PassengersIn]
      ,[edw].[MPC_MpcTripSummary].[PassengersOut]
      ,[edw].[MPC_MpcTripSummary].[CarsIn]
      ,[edw].[MPC_MpcTripSummary].[CarsOut]
      ,[edw].[MPC_MpcTripSummary].[Cars7mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars7mOut]
      ,[edw].[MPC_MpcTripSummary].[Cars8mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars8mOut]
      ,[edw].[MPC_MpcTripSummary].[Cars10mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars10mOut]
      ,[edw].[MPC_MpcTripSummary].[Cars12mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars12mOut]
      ,[edw].[MPC_MpcTripSummary].[Cars14mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars14mOut]
      ,[edw].[MPC_MpcTripSummary].[Cars17mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars17mOut]
      ,[edw].[MPC_MpcTripSummary].[Cars19mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars19mOut]
      ,[edw].[MPC_MpcTripSummary].[Cars22mIn]
      ,[edw].[MPC_MpcTripSummary].[Cars22mOut]
      ,[edw].[MPC_MpcTripSummary].[MotorcyclesIn]
      ,[edw].[MPC_MpcTripSummary].[MotorcyclesOut]
      ,[edw].[MPC_MpcTripSummary].[CarsLeftBehind]
      ,[edw].[MPC_MpcTripSummary].[QuayRef]
      ,[edw].[MPC_MpcTripSummary].[PublicCode]
      ,[edw].[MPC_MpcTripSummary].[PassingTime]
      ,[edw].[MPC_MpcTripSummary].[TransportType]
      ,[edw].[MPC_MpcTripSummary].[Latitude]
      ,[edw].[MPC_MpcTripSummary].[Longitude]
      ,[edw].[MPC_MpcTripSummary].[CompanyNumber]
      ,[edw].[MPC_MpcTripSummary].[DeviceId]
      ,[edw].[MPC_MpcTripSummary].[LastEventTimestamp]
      ,[arc].[TTDB_LINES].[PublicCode] AS Route_short_name
      ,[arc].[TTDB_Quays].[KolumbusId]
  FROM [edw].[MPC_MpcTripSummary]
LEFT JOIN [arc].[TTDB_Lines] ON LEFT([edw].[MPC_MpcTripSummary].TripId, 4) = [arc].[TTDB_Lines].[PrivateCode]
LEFT JOIN [arc].[TTDB_Quays] ON [edw].[MPC_MpcTripSummary].[QuayRef] = arc.[TTDB_Quays].[QuayRef]
GO

-------------------------------------------------------------------------------
-- edw.V_MpcTripSummary_Tidebuss
-------------------------------------------------------------------------------
CREATE OR ALTER VIEW [edw].[V_MpcTripSummary_Tidebuss]
AS
SELECT
       [edw].[MPC_MpcTripSummary].[MpcTripId]
      ,[edw].[MPC_MpcTripSummary].[TripId]
      ,[edw].[MPC_MpcTripSummary].[TripStatus]
      ,[edw].[MPC_MpcTripSummary].[TripLastModified]
      ,[edw].[MPC_MpcTripSummary].[StopName]
      ,[edw].[MPC_MpcTripSummary].[PassengersIn]
      ,[edw].[MPC_MpcTripSummary].[PassengersOut]
      ,[edw].[MPC_MpcTripSummary].[QuayRef]
      ,[edw].[MPC_MpcTripSummary].[PublicCode]
      ,[edw].[MPC_MpcTripSummary].[PassingTime]
      ,[edw].[MPC_MpcTripSummary].[TransportType]
      ,[edw].[MPC_MpcTripSummary].[Latitude]
      ,[edw].[MPC_MpcTripSummary].[Longitude]
      ,[edw].[MPC_MpcTripSummary].[CompanyNumber]
      ,[edw].[MPC_MpcTripSummary].[DeviceId]
      ,[edw].[MPC_MpcTripSummary].[LastEventTimestamp]
      ,[arc].[TTDB_LINES].[PublicCode] AS Route_short_name
      ,[arc].[TTDB_Quays].[KolumbusId]
  FROM [edw].[MPC_MpcTripSummary]
LEFT JOIN [arc].[TTDB_Lines] ON LEFT([edw].[MPC_MpcTripSummary].TripId, 4) = [arc].[TTDB_Lines].[PrivateCode]
LEFT JOIN [arc].[TTDB_Quays] ON [edw].[MPC_MpcTripSummary].[QuayRef] = arc.[TTDB_Quays].[QuayRef]
WHERE CompanyNumber = '312' AND TransportType = 'bus'
GO


