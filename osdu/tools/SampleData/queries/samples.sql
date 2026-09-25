-- The curve values of every Recall log the sample exports, one row per sample, numbered per curve in the order the source
-- stores them: the position recall_to_osdu's prepare turns into a measured depth (TopDepth + position * increment).
-- :table is the curated log-curve table of the log source, :log_name the log source, :wellbores the UWIs joined by '|'.
SELECT
  row_key,
  row_number() OVER (PARTITION BY row_key ORDER BY sample_ordinal) - 1 AS sample_index,
  value_field,
  CASE value_field
    WHEN 'data_double' THEN CAST(data_double AS STRING)
    WHEN 'data_float' THEN CAST(data_float AS STRING)
    WHEN 'data_int' THEN CAST(data_int AS STRING)
  END AS value
FROM IDENTIFIER(:table)
WHERE log_name = :log_name
  AND array_contains(split(:wellbores, '[|]'), wellbore_uwi)
ORDER BY row_key, sample_index
