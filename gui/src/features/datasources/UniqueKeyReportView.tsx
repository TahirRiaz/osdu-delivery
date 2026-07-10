import { useState } from "react";
import Alert from "@mui/material/Alert";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import ExpandLessIcon from "@mui/icons-material/ExpandLess";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
import type { UniqueKeyCandidate, UniqueKeyReport } from "../../api/types";
import { CopyButton } from "../../components/CopyButton";
import { DataTable, type Column } from "../../components/DataTable";
import { Mono } from "../../components/Mono";

/** One unique-key candidate line: the verdict and how trustworthy it is. */
function candidateLabel(candidate: UniqueKeyCandidate): string {
  if (candidate.isUnique) {
    if (candidate.declared) {
      return "UNIQUE (declared by the database)";
    }

    return candidate.verified ? "UNIQUE (verified against the whole table)" : "unique on the sample (unverified)";
  }

  const approx = candidate.estimated ? "~" : "";
  return `not unique (${approx}${candidate.duplicates.toLocaleString()} duplicate row(s)${candidate.nulls > 0 ? `, ${approx}${candidate.nulls.toLocaleString()} null row(s)` : ""}${candidate.estimated ? ", sample estimate" : ""})`;
}

/** The flow-file fragment for one detected key, ready to paste into a flow's YAML. */
function keyColumnsYaml(columns: string[]): string {
  return `keyColumns:\n${columns.map((c) => `  - ${c}`).join("\n")}\n`;
}

/** Selectivity formatted for display: 1 collapses to "1", everything else keeps four decimals. */
function formatSelectivity(value: number): string {
  return value === 1 ? "1" : value.toFixed(4);
}

const statisticsColumns: Column<UniqueKeyReport["columns"][number]>[] = [
  { id: "column", header: "Column", render: (row) => <Mono>{row.column}</Mono> },
  { id: "distinct", header: "Distinct", align: "right", render: (row) => row.distinct.toLocaleString() },
  { id: "nulls", header: "Nulls", align: "right", render: (row) => row.nulls.toLocaleString() },
  {
    id: "selectivity",
    header: "Selectivity",
    align: "right",
    render: (row) => <Mono>{formatSelectivity(row.selectivity)}</Mono>,
  },
];

interface UniqueKeyReportViewProps {
  report: UniqueKeyReport;
  /** Compact spacing for the inspector drawer; the dedicated page uses the roomier default. */
  dense?: boolean;
  "data-testid"?: string;
}

/**
 * The one rendering of a unique-key detection report, shared by the inspector drawer and the dedicated
 * detection page: the scan scope, the ranked candidates (with selectivity and per-candidate keyColumns copy),
 * the per-column statistics (sorted most-identifying first, collapsed by default), the columns excluded before
 * the search with their reasons, and the report note. Purely presentational; running the detection is the
 * caller's concern.
 */
export function UniqueKeyReportView({ report, dense = false, "data-testid": testId }: UniqueKeyReportViewProps) {
  const [showStatistics, setShowStatistics] = useState(false);
  const metadataAnswer = report.scannedRows === 0 && report.candidates.some((c) => c.declared);

  const scope = metadataAnswer
    ? `${report.totalRows.toLocaleString()} row(s), answered from database metadata without reading the data`
    : report.sampled
      ? `${report.totalRows.toLocaleString()} row(s), profiled on a random sample of ${report.scannedRows.toLocaleString()}`
      : `${report.totalRows.toLocaleString()} row(s), full scan`;

  const statistics = [...report.columns].sort((a, b) => b.selectivity - a.selectivity);

  return (
    <Stack spacing={dense ? 1 : 1.5} data-testid={testId ?? "unique-key-report"}>
      <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
        <Typography variant="body2" color="text.secondary" data-testid="report-scope">{scope}</Typography>
        <CopyButton
          label="Copy report JSON"
          text={JSON.stringify(report, null, 2)}
          testId="copy-report-json"
        />
      </Stack>

      {report.candidates.length === 0 && (
        <Alert severity="warning" data-testid="report-no-candidates">
          No candidate key was found.
        </Alert>
      )}

      {report.candidates.map((candidate, rank) => (
        <Alert
          key={candidate.columns.join("|")}
          severity={candidate.isUnique ? "success" : "info"}
          icon={false}
          data-testid="report-candidate"
        >
          <Stack spacing={0.5}>
            <Stack direction="row" spacing={1} alignItems="center" flexWrap="wrap" useFlexGap>
              <Typography variant="body2" fontWeight={600}>{rank + 1}.</Typography>
              <Mono sx={{ fontWeight: 600 }}>[{candidate.columns.join(", ")}]</Mono>
              {candidate.declared && <Chip size="small" variant="outlined" color="primary" label="declared" />}
              {candidate.isUnique && !candidate.declared && candidate.verified && (
                <Chip size="small" variant="outlined" color="success" label="verified" />
              )}
              {candidate.estimated && <Chip size="small" variant="outlined" label="sample estimate" />}
              <Chip
                size="small"
                variant="outlined"
                label={`selectivity ${candidate.estimated ? "~" : ""}${formatSelectivity(candidate.selectivity)}`}
              />
            </Stack>
            <Stack direction="row" spacing={1} alignItems="center" justifyContent="space-between" flexWrap="wrap" useFlexGap>
              <Typography variant="body2">{candidateLabel(candidate)}</Typography>
              {candidate.isUnique && (
                <CopyButton
                  label="Copy keyColumns YAML"
                  text={keyColumnsYaml(candidate.columns)}
                  testId="copy-key-columns"
                />
              )}
            </Stack>
          </Stack>
        </Alert>
      ))}

      {statistics.length > 0 && (
        <>
          <Button
            size="small"
            color="inherit"
            sx={{ alignSelf: "flex-start" }}
            startIcon={showStatistics ? <ExpandLessIcon /> : <ExpandMoreIcon />}
            onClick={() => setShowStatistics((current) => !current)}
            data-testid="toggle-column-statistics"
          >
            Column statistics ({statistics.length})
          </Button>
          {showStatistics && (
            <DataTable
              columns={statisticsColumns}
              rows={statistics}
              rowKey={(row) => row.column}
              emptyMessage="No columns were measured."
              data-testid="column-statistics-table"
            />
          )}
        </>
      )}

      {report.excludedColumns.length > 0 && (
        <Typography variant="caption" color="text.secondary" data-testid="report-excluded">
          Excluded from the search: {report.excludedColumns.map((e) => `${e.column} (${e.reason})`).join(", ")}
        </Typography>
      )}

      {report.note !== null && (
        <Typography variant="caption" color="text.secondary" data-testid="report-note">{report.note}</Typography>
      )}
    </Stack>
  );
}
