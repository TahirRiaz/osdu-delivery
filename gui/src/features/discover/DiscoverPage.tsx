import { useMemo, useState } from "react";
import {
  Accordion, AccordionDetails, AccordionSummary, Alert, Box, Button, Chip, CircularProgress, Divider,
  FormControlLabel, MenuItem, Paper, Stack, Switch, TextField, Tooltip, Typography,
} from "@mui/material";
import TravelExploreIcon from "@mui/icons-material/TravelExplore";
import DownloadIcon from "@mui/icons-material/Download";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
import TuneIcon from "@mui/icons-material/Tune";
import { useMutation } from "@tanstack/react-query";
import { useSnackbar } from "notistack";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { CodeView } from "../../components/CodeView";
import { Column, DataTable } from "../../components/DataTable";
import { CorrelationError } from "../../components/CorrelationError";
import { isApiError } from "../../api/client";
import { sourceApi } from "../../api/endpoints";
import type { DiscoveredColumn, DiscoveredPath, SourceDiscoverResult } from "../../api/types";

const FORMATS = [
  { value: "", label: "Auto (detect from content)" },
  { value: "json", label: "JSON" },
  { value: "ndjson", label: "NDJSON" },
  { value: "jsonl", label: "JSONL" },
  { value: "xml", label: "XML" },
  { value: "csv", label: "CSV" },
  { value: "tsv", label: "TSV (tab)" },
  { value: "xlsx", label: "Excel (xls/xlsx)" },
  { value: "parquet", label: "Parquet" },
];

const CONFIDENCE_COLOR: Record<string, "success" | "info" | "warning" | "default"> = {
  explicit: "default",
  extension: "info",
  high: "success",
  medium: "warning",
  low: "warning",
};

/** Parses a text number field into a positive integer, or undefined when blank/invalid (falls back to the default). */
function parseCount(raw: string): number | undefined {
  const trimmed = raw.trim();
  if (trimmed === "") {
    return undefined;
  }
  const value = Number(trimmed);
  return Number.isFinite(value) && value >= 0 ? Math.floor(value) : undefined;
}

/** A download filename derived from the source location's last segment (folder or file), always *.flow.yaml. */
function yamlFileName(location: string): string {
  const cleaned = location.split(/[?#]/)[0].replace(/[/\\]+$/, "");
  const segment = cleaned.split(/[/\\]/).pop() ?? "source";
  const base = segment.replace(/\.[^.]+$/, "").replace(/[^a-zA-Z0-9]+/g, "_").replace(/^_+|_+$/g, "");
  return `${base || "source"}.flow.yaml`;
}

function presenceLabel(row: DiscoveredPath, recordsScanned: number): string {
  return row.present ? "all" : `${row.recordCount}/${recordsScanned}`;
}

export default function DiscoverPage() {
  const { enqueueSnackbar } = useSnackbar();

  const [location, setLocation] = useState("");
  const [format, setFormat] = useState("");
  const [pattern, setPattern] = useState("");
  const [recursive, setRecursive] = useState(false);
  const [rootPath, setRootPath] = useState("");
  const [maxFiles, setMaxFiles] = useState("");
  const [maxRecords, setMaxRecords] = useState("");
  const [maxDepth, setMaxDepth] = useState("");

  const discover = useMutation({
    mutationFn: (): Promise<SourceDiscoverResult> =>
      sourceApi.discover({
        location: location.trim(),
        format: format || null,
        pattern: pattern.trim() || null,
        recursive,
        rootPath: rootPath.trim() || null,
        maxFiles: parseCount(maxFiles) ?? null,
        maxRecords: parseCount(maxRecords) ?? null,
        maxDepth: parseCount(maxDepth) ?? null,
      }),
    onError: (error) =>
      enqueueSnackbar(error instanceof Error ? error.message : String(error), { variant: "error" }),
  });

  const result = discover.data;
  const error = discover.error;

  const pathColumns: Column<DiscoveredPath>[] = useMemo(
    () => [
      { id: "path", header: "Path", render: (row) => <Typography variant="body2" sx={{ fontFamily: "monospace" }}>{row.path}</Typography> },
      { id: "column", header: "Column", render: (row) => <Typography variant="body2" sx={{ fontFamily: "monospace" }}>{row.column}</Typography> },
      { id: "kind", header: "Kind", render: (row) => <Chip size="small" variant="outlined" label={row.kind} /> },
      {
        id: "presence",
        header: "Presence",
        align: "right",
        render: (row) => (result ? presenceLabel(row, result.recordsScanned) : ""),
      },
    ],
    [result],
  );

  const columnColumns: Column<DiscoveredColumn>[] = useMemo(
    () => [
      { id: "name", header: "Column", render: (row) => <Typography variant="body2" sx={{ fontFamily: "monospace" }}>{row.name}</Typography> },
      { id: "type", header: "Type", render: (row) => <Chip size="small" variant="outlined" label={row.sqlType} /> },
      { id: "nullable", header: "Nullable", align: "right", render: (row) => (row.nullable ? "yes" : "no") },
    ],
    [],
  );

  const canDiscover = location.trim().length > 0 && !discover.isPending;
  const advancedCount = [format, pattern.trim(), rootPath.trim(), maxFiles.trim(), maxRecords.trim(), maxDepth.trim()]
    .filter(Boolean).length + (recursive ? 1 : 0);

  const downloadYaml = () => {
    if (!result) {
      return;
    }
    const blob = new Blob([result.generatedYaml], { type: "text/yaml;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = yamlFileName(location);
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  };

  const submit = () => {
    if (canDiscover) {
      discover.mutate();
    }
  };

  return (
    <Page data-testid="discover-page">
      <PageHeader
        title="Discover"
        subtitle="Point at any file or folder (JSON, XML, CSV, Excel, Parquet); SQLFlow detects the format, samples it, and generates the ingestion YAML."
      />

      <Paper variant="outlined" sx={{ p: 3 }}>
        <Stack spacing={2}>
          <Stack direction={{ xs: "column", sm: "row" }} spacing={1.5} alignItems={{ sm: "flex-start" }}>
            <TextField
              label="Source location"
              placeholder="abfss://container/path/ (folder or file) — format is detected automatically"
              value={location}
              onChange={(e) => setLocation(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") {
                  submit();
                }
              }}
              fullWidth
              required
              autoFocus
              data-testid="discover-location"
            />
            <Button
              variant="contained"
              size="large"
              startIcon={discover.isPending ? <CircularProgress size={18} color="inherit" /> : <TravelExploreIcon />}
              disabled={!canDiscover}
              onClick={submit}
              sx={{ height: 56, px: 3, flexShrink: 0 }}
              data-testid="discover-submit"
            >
              {discover.isPending ? "Discovering..." : "Discover"}
            </Button>
          </Stack>

          <Accordion disableGutters elevation={0} sx={{ bgcolor: "transparent", "&:before": { display: "none" } }}>
            <AccordionSummary expandIcon={<ExpandMoreIcon />} sx={{ px: 0, minHeight: 0 }} data-testid="discover-advanced-toggle">
              <Stack direction="row" spacing={1} alignItems="center" color="text.secondary">
                <TuneIcon fontSize="small" />
                <Typography variant="body2">
                  Advanced{advancedCount > 0 ? ` (${advancedCount} set)` : ""}
                </Typography>
              </Stack>
            </AccordionSummary>
            <AccordionDetails sx={{ px: 0, pt: 0 }}>
              <Stack spacing={2}>
                <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
                  <TextField
                    select
                    label="Format"
                    value={format}
                    onChange={(e) => setFormat(e.target.value)}
                    sx={{ minWidth: 260 }}
                    helperText="Auto detects from the file content."
                    data-testid="discover-format"
                  >
                    {FORMATS.map((f) => (
                      <MenuItem key={f.value} value={f.value}>{f.label}</MenuItem>
                    ))}
                  </TextField>
                  <TextField
                    label="Folder pattern"
                    placeholder="auto per format"
                    value={pattern}
                    onChange={(e) => setPattern(e.target.value)}
                    sx={{ minWidth: 160 }}
                    helperText="Used only for a folder location."
                    data-testid="discover-pattern"
                  />
                  <FormControlLabel
                    control={<Switch checked={recursive} onChange={(e) => setRecursive(e.target.checked)} data-testid="discover-recursive" />}
                    label="Recurse sub-folders"
                  />
                </Stack>

                <Stack direction={{ xs: "column", sm: "row" }} spacing={2}>
                  <TextField
                    label="Record grain override"
                    placeholder="rootPath / rowXPath (JSON/XML, auto if blank)"
                    value={rootPath}
                    onChange={(e) => setRootPath(e.target.value)}
                    sx={{ flex: 1, minWidth: 240 }}
                    data-testid="discover-rootpath"
                  />
                  <TextField
                    label="Max files"
                    placeholder="100"
                    value={maxFiles}
                    onChange={(e) => setMaxFiles(e.target.value)}
                    sx={{ width: 120 }}
                    inputProps={{ inputMode: "numeric" }}
                    data-testid="discover-maxfiles"
                  />
                  <TextField
                    label="Max records"
                    placeholder="0 = all"
                    value={maxRecords}
                    onChange={(e) => setMaxRecords(e.target.value)}
                    sx={{ width: 120 }}
                    inputProps={{ inputMode: "numeric" }}
                    data-testid="discover-maxrecords"
                  />
                  <TextField
                    label="Max depth"
                    placeholder="10"
                    value={maxDepth}
                    onChange={(e) => setMaxDepth(e.target.value)}
                    sx={{ width: 120 }}
                    inputProps={{ inputMode: "numeric" }}
                    data-testid="discover-maxdepth"
                  />
                </Stack>
              </Stack>
            </AccordionDetails>
          </Accordion>

          {error && (
            isApiError(error)
              ? <CorrelationError error={error} />
              : <Typography color="error">{String(error)}</Typography>
          )}
        </Stack>
      </Paper>

      {result && (
        <Stack spacing={3}>
          <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap alignItems="center">
            <Chip color="primary" label={result.sourceType.toUpperCase()} data-testid="discover-format-chip" />
            <Tooltip title={result.detectionEvidence.join("; ")}>
              <Chip
                size="small"
                color={CONFIDENCE_COLOR[result.detectionConfidence] ?? "default"}
                variant="outlined"
                label={`format: ${result.detectionConfidence}`}
                data-testid="discover-confidence-chip"
              />
            </Tooltip>
            <Chip variant="outlined" label={`${result.filesScanned} file(s)`} />
            {result.mode === "flatten" ? (
              <>
                <Chip variant="outlined" label={`${result.recordsScanned} record(s)`} />
                <Chip variant="outlined" label={`${result.paths.length} path(s)`} />
                {result.autoDetectedGrain && (
                  <Chip color="info" variant="outlined" label={`grain: ${result.autoDetectedGrain}`} />
                )}
                {result.schemaDrift && <Chip color="warning" variant="outlined" label="schema drift" />}
              </>
            ) : (
              <Chip variant="outlined" label={`${result.columns.length} column(s)`} />
            )}
          </Stack>

          {result.mode === "flatten" && result.schemaDrift && (
            <Alert severity="warning">
              Some paths are missing from part of the sample. A path missing from a file becomes NULL for that file&apos;s
              rows; reconcile renamed fields with <code>pathAliases</code>.
            </Alert>
          )}

          {result.mode === "flatten" ? (
            result.paths.length === 0 ? (
              <Alert severity="info">
                No records found. Check the location, the folder pattern, and the record grain (rootPath / rowXPath).
              </Alert>
            ) : (
              <Box>
                <Typography variant="subtitle1" gutterBottom>Path structure</Typography>
                <DataTable
                  columns={pathColumns}
                  rows={result.paths}
                  rowKey={(row) => row.path}
                  rowSx={(row) => (row.present ? undefined : { backgroundColor: "rgba(237, 108, 2, 0.08)" })}
                  emptyMessage="No paths discovered."
                  data-testid="discover-paths"
                />
              </Box>
            )
          ) : (
            <Box>
              <Typography variant="subtitle1" gutterBottom>Columns</Typography>
              <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
                Schema sampled from a representative file. A tabular source lands string-first; typing happens in a later
                transformation stage.
              </Typography>
              <DataTable
                columns={columnColumns}
                rows={result.columns}
                rowKey={(row) => row.name}
                emptyMessage="No columns discovered."
                data-testid="discover-columns"
              />
            </Box>
          )}

          <Box>
            <Stack direction="row" alignItems="center" justifyContent="space-between" sx={{ mb: 1 }}>
              <Typography variant="subtitle1">Ingestion YAML</Typography>
              <Button
                size="small"
                variant="outlined"
                startIcon={<DownloadIcon />}
                onClick={downloadYaml}
                data-testid="discover-download"
              >
                Download
              </Button>
            </Stack>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
              A runnable ingestion flow. Set <code>target.connection</code>, then run it to ingest the source.
            </Typography>
            <Divider sx={{ mb: 1 }} />
            <CodeView value={result.generatedYaml} language="yaml" lsp={false} height={420} data-testid="discover-yaml" />
          </Box>
        </Stack>
      )}
    </Page>
  );
}
