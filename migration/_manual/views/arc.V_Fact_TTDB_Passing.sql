

CREATE VIEW [arc].[V_Fact_TTDB_Passing]
as
SELECT       f.[tripFactsPK],
             f.operatingDay,
             f.[tripPK],
             f.[linePK],
             f.[routeBK],
             f.scheduleBK,
             p.stopNr,
             p.deltaTime,
             p.waitTime,
             p.quayNsr,
             q.quayPk,
			 q.[stopId],
             DATEADD(
                 MINUTE,
                 accTime + accWait - waitTime,
                 CAST(DATEADD(DAY, offset, operatingDay) AS DATETIME) + CAST(departureTime AS DATETIME)) AS arrivalTime,
             DATEADD(
                 MINUTE,
                 accTime + accWait,
                 CAST(DATEADD(DAY, offset, operatingDay) AS DATETIME) + CAST(departureTime AS DATETIME)) AS departureTime
  FROM       arc.TTDB_TripFacts f
  JOIN       arc.TTDB_Trip t
    ON t.tripPK = f.tripPK
  JOIN       arc.TTDB_Passing p
    ON p.scheduleBK = f.scheduleBK
 OUTER APPLY (   SELECT TOP 1 quayPk,
                              [quayNsr],
							  [stopId]
                   FROM arc.TTDB_Quay q
                  WHERE q.quayNsr      = p.quayNsr
                    AND f.operatingDay <= [dw_valid_from]
                  ORDER BY [dw_valid_from] DESC) q

