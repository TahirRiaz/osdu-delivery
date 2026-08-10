CREATE VIEW [edw].[V_MpcTripSummary_Tidebuss]
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

