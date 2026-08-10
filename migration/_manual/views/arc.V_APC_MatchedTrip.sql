














CREATE VIEW [arc].[V_APC_MatchedTrip]
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



