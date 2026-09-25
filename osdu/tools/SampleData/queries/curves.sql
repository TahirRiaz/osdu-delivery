-- One row per curve of every Recall log the sample exports: the log's header columns beside the curve's own, read the way
-- recall_to_osdu's build_metadata_df reads them (wl-pipelines packages/recall_to_osdu/src/recall_to_osdu/utils/builders.py).
-- :table is the enriched Recall log-curve table, :log_name the log source, and :wellbores the wellbores' UWIs joined by '|'.
SELECT
  `recallcommonmodel:_source_project`          AS source_project,
  `recallcommonmodel:WellLog__wellbore_uwi`    AS wellbore_uwi,
  `recallcommonmodel:log_name`                 AS log_name,
  `recall:LOG_ID`                              AS log_id,
  `recall:LOG_RUN`                             AS log_run,
  `recall:LOG_SOURCE`                          AS recall_log_source,
  `recallcommonmodel:WellLog__index_min`       AS log_index_min,
  `recallcommonmodel:WellLog__index_max`       AS log_index_max,
  `recallcommonmodel:WellLog__index_min_unit`  AS log_index_unit,
  `recallcommonmodel:WellLog__index_type`      AS index_type,
  `recallcommonmodel:index_increment`          AS index_increment,
  `recall:CREATOR`                             AS creator,
  `recall:LOG_SERVICE`                         AS log_service,
  `recallcommonmodel:WellLog__log_version`     AS log_version,
  `recall:DATA_TYPE`                           AS data_type,
  `recall:LOGGING_CONTRACTOR`                  AS logging_contractor,
  `recall:LOG_PASS`                            AS log_pass,
  `recall:ELEV_MEAS_REF`                       AS elev_meas_ref,
  `recall:ELEV_MEAS_REF_DSDSUNIT`              AS elev_meas_ref_unit,
  `recall:LOGS_MEAS_FROM`                      AS logs_meas_from,
  `recallcommonmodel:WellLog__log_pass_type`   AS log_pass_type,
  `recallcommonmodel:WellLog__native_uid`      AS native_uid,
  `recallcommonmodel:WellLog__update_date`     AS log_update_date,
  `recallcommonmodel:log_curve_name`           AS curve_id,
  `recallcommonmodel:curve_unit`               AS curve_unit,
  `recallcommonmodel:index_unit`               AS curve_index_unit,
  `recallcommonmodel:index_min`                AS curve_index_min,
  `recallcommonmodel:index_max`                AS curve_index_max,
  `recallcommonmodel:curve_description`        AS curve_description,
  `recallcommonmodel:log_curve_version`        AS curve_version,
  `recall_curve:BUSINESS_VALUE`                AS business_value,
  `recall_curve:DEPTH_CODING`                  AS depth_coding,
  `recallcommonmodel:update_date`              AS curve_update_date,
  `recallcommonmodel:_dsis_row_key`            AS row_key
FROM IDENTIFIER(:table)
WHERE `recallcommonmodel:log_name` = :log_name
  AND array_contains(split(:wellbores, '[|]'), `recallcommonmodel:WellLog__wellbore_uwi`)
ORDER BY wellbore_uwi, source_project, log_id, curve_id
