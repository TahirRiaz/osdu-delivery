import { useEffect, useMemo, useState } from "react";
import { useQuery, useMutation } from "@tanstack/react-query";
import Accordion from "@mui/material/Accordion";
import AccordionDetails from "@mui/material/AccordionDetails";
import AccordionSummary from "@mui/material/AccordionSummary";
import Alert from "@mui/material/Alert";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import CircularProgress from "@mui/material/CircularProgress";
import Dialog from "@mui/material/Dialog";
import DialogContent from "@mui/material/DialogContent";
import DialogTitle from "@mui/material/DialogTitle";
import Divider from "@mui/material/Divider";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import BugReportIcon from "@mui/icons-material/BugReport";
import CableIcon from "@mui/icons-material/Cable";
import CodeIcon from "@mui/icons-material/Code";
import ExpandMoreIcon from "@mui/icons-material/ExpandMore";
import RefreshIcon from "@mui/icons-material/Refresh";
import { get, post, isApiError } from "../../api/client";
import { pipelineApi } from "../../api/endpoints";
import { CodeView } from "../../components/CodeView";
import { DataTable, type Column } from "../../components/DataTable";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";

interface Integration {
  id: string;
  name: string;
  batch: string | null;
  relativePath: string;
  active: boolean;
}

interface IntegrationDebugPage {
  iteration: number;
  page: number;
  method: string;
  url: string;
  requestHeaders: Record<string, string>;
  status: number;
  responseHeaders: Record<string, string>;
  contentType: string | null;
  bytes: number;
  recordCount: number;
  durationMs: number;
  bodyPreview: string;
  wouldLandTo: string | null;
}

interface IntegrationDebugResponse {
  success: boolean;
  error: string | null;
  iterations: number;
  pagesFetched: number;
  filesWritten: number;
  skipped: number;
  bytesWritten: number;
  landedBase: string | null;
  watermarkBefore: string | null;
  watermarkAfter: string | null;
  pages: IntegrationDebugPage[];
}

interface IntegrationParam {
  name: string;
  default: string | null;
  required: boolean;
}

interface IntegrationDescribeResponse {
  name: string;
  batch: string | null;
  transport: string;
  hasDateWindow: boolean;
  params: IntegrationParam[];
}

const integrationApi = {
  list: () => get<Integration[]>("/api/v1/integrations"),
  describe: (yaml: string) => post<IntegrationDescribeResponse>("/api/v1/integrations/describe", { yaml }),
  debug: (yaml: string, maxPages: number, params: Record<string, string>) =>
    post<IntegrationDebugResponse>("/api/v1/integrations/debug", {
      yaml,
      maxPages,
      params: Object.keys(params).length > 0 ? params : undefined,
    }),
};

const STARTER_YAML = `flowType: acq
name: My_Integration
source:
  transport: http
  baseUrl: https://jsonplaceholder.typicode.com
  request:
    method: GET
    path: /posts
  reliability:
    urlAllowlist: ["*.typicode.com"]
landing:
  target: ./_landing/my_integration
  pathTemplate: posts_{yyyyMMdd}
`;

function looksJson(page: IntegrationDebugPage): boolean {
  const media = page.contentType?.toLowerCase() ?? "";
  return media.includes("json") || page.bodyPreview.trimStart().startsWith("{") || page.bodyPreview.trimStart().startsWith("[");
}

/** Loads an integration's stored (secret-redacted) YAML from the catalog by its pipeline id. */
function useIntegrationYaml(id: string | null) {
  return useQuery({
    queryKey: ["integrations", "yaml", id],
    queryFn: () => pipelineApi.getById(id as string).then((p) => p.yaml),
    enabled: id !== null,
  });
}

/** Read-only view of the integration's YAML document exactly as the catalog stores it (secrets redacted). */
function CodeDialog({ integration, onClose, onDebug }: { integration: Integration; onClose: () => void; onDebug: (yaml: string) => void }) {
  const yaml = useIntegrationYaml(integration.id);

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="md" data-testid="integration-code">
      <DialogTitle>
        <Stack direction="row" spacing={1} alignItems="center" sx={{ minWidth: 0 }}>
          <CodeIcon fontSize="small" />
          <Mono sx={{ overflow: "hidden", textOverflow: "ellipsis" }}>{integration.relativePath}</Mono>
        </Stack>
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          {yaml.isLoading && <CircularProgress size={22} />}
          {yaml.isError && <Alert severity="error">Could not load the definition from the catalog.</Alert>}
          {yaml.data !== undefined && (
            <>
              <CodeView value={yaml.data} language="yaml" height={480} data-testid="integration-code-view" />
              <Stack direction="row" spacing={1} justifyContent="flex-end">
                <Button startIcon={<BugReportIcon />} variant="contained" onClick={() => onDebug(yaml.data)}>
                  Open in debugger
                </Button>
              </Stack>
            </>
          )}
        </Stack>
      </DialogContent>
    </Dialog>
  );
}

/**
 * The dynamically generated parameter form: the backend parses the YAML through the real loader (debounced as the
 * user edits) and reports the declared `params:`; one input renders per parameter, prefilled with its default and
 * required-marked when it declares none. The values feed the run as overrides, so executing a parameterized
 * integration never means editing its YAML.
 */
function useDeclaredParams(yaml: string) {
  const [described, setDescribed] = useState<IntegrationDescribeResponse | null>(null);
  useEffect(() => {
    let cancelled = false;
    const handle = setTimeout(() => {
      integrationApi
        .describe(yaml)
        .then((d) => {
          if (!cancelled) {
            setDescribed(d);
          }
        })
        .catch(() => {
          // A document mid-edit often does not parse; keep the last good description until it does again.
        });
    }, 500);
    return () => {
      cancelled = true;
      clearTimeout(handle);
    };
  }, [yaml]);
  return described;
}

/** The DeltaForge-style debugger: edit the integration YAML in Monaco (flow-YAML hovers and diagnostics on), fill
 * the auto-generated parameter form, run a safe Test invoke (fetch without landing), and inspect the request,
 * response, timing, pagination steps, and where each page would land. */
function DebuggerDialog({ initialYaml, onClose }: { initialYaml: string; onClose: () => void }) {
  const [yaml, setYaml] = useState(initialYaml);
  const [maxPages, setMaxPages] = useState(3);
  const [paramValues, setParamValues] = useState<Record<string, string>>({});
  const described = useDeclaredParams(yaml);

  const debug = useMutation({
    mutationFn: () => {
      // Send only the values the user actually set; unset fields fall back to the declared defaults server-side.
      const supplied = Object.fromEntries(Object.entries(paramValues).filter(([, v]) => v.length > 0));
      return integrationApi.debug(yaml, maxPages, supplied);
    },
  });

  const missingRequired = (described?.params ?? []).filter((p) => p.required && !(paramValues[p.name]?.length > 0));

  const errorText = debug.isError
    ? isApiError(debug.error)
      ? debug.error.detail ?? debug.error.title
      : String(debug.error)
    : null;
  const result = debug.data;

  return (
    <Dialog open onClose={onClose} fullWidth maxWidth="lg" data-testid="integration-debugger">
      <DialogTitle>
        <Stack direction="row" spacing={1} alignItems="center">
          <BugReportIcon fontSize="small" />
          <span>Integration debugger</span>
        </Stack>
      </DialogTitle>
      <DialogContent dividers>
        <Stack spacing={2}>
          <Alert severity="info">
            Test is a safe preview: it fetches (honoring auth, pagination, and iteration) but lands nothing. It shows
            what a production run would write.
          </Alert>

          <CodeView
            value={yaml}
            language="yaml"
            height={360}
            readOnly={false}
            onChange={setYaml}
            data-testid="integration-debugger-yaml"
          />

          {described !== null && described.params.length > 0 && (
            <Box>
              <Typography variant="subtitle2" gutterBottom>
                Parameters
              </Typography>
              <Stack direction="row" spacing={1.5} useFlexGap flexWrap="wrap">
                {described.params.map((param) => (
                  <TextField
                    key={param.name}
                    label={param.name}
                    size="small"
                    required={param.required}
                    value={paramValues[param.name] ?? ""}
                    placeholder={param.default ?? undefined}
                    helperText={param.required ? "required (no default)" : `default: ${param.default}`}
                    onChange={(e) => setParamValues((current) => ({ ...current, [param.name]: e.target.value }))}
                    sx={{ minWidth: 220 }}
                    data-testid={`integration-param-${param.name}`}
                  />
                ))}
              </Stack>
            </Box>
          )}

          <Stack direction="row" spacing={2} alignItems="center">
            <TextField
              label="Max pages"
              type="number"
              size="small"
              value={maxPages}
              onChange={(e) => setMaxPages(Math.max(1, Math.min(20, Number(e.target.value) || 1)))}
              sx={{ width: 130 }}
              slotProps={{ htmlInput: { min: 1, max: 20 } }}
            />
            <Button
              variant="contained"
              startIcon={debug.isPending ? <CircularProgress size={16} color="inherit" /> : <BugReportIcon />}
              disabled={debug.isPending || missingRequired.length > 0}
              onClick={() => debug.mutate()}
              data-testid="integration-debugger-run"
            >
              Run test
            </Button>
            {missingRequired.length > 0 && (
              <Typography variant="caption" color="text.secondary">
                Supply {missingRequired.map((p) => p.name).join(", ")} to run.
              </Typography>
            )}
          </Stack>

          {errorText && <Alert severity="error">{errorText}</Alert>}

          {result && (
            <Stack spacing={2}>
              <Divider />
              <Stack direction="row" spacing={1} flexWrap="wrap" useFlexGap>
                <Chip
                  color={result.success ? "success" : "error"}
                  label={result.success ? "success" : "failed"}
                  size="small"
                />
                <Chip label={`${result.iterations} iteration(s)`} size="small" variant="outlined" />
                <Chip label={`${result.pagesFetched} page(s) fetched`} size="small" variant="outlined" />
                <Chip label={`${result.filesWritten} would land`} size="small" variant="outlined" />
                <Chip label={`${result.skipped} skipped`} size="small" variant="outlined" />
                <Chip label={`${result.bytesWritten} bytes`} size="small" variant="outlined" />
              </Stack>
              {result.landedBase && (
                <Typography variant="body2" color="text.secondary">
                  Would land under <Mono>{result.landedBase}</Mono>
                  {result.watermarkAfter && (
                    <>
                      {" · watermark "}
                      <Mono>{result.watermarkBefore ?? "(none)"}</Mono> → <Mono>{result.watermarkAfter}</Mono>
                    </>
                  )}
                </Typography>
              )}
              {result.error && <Alert severity="warning">{result.error}</Alert>}

              {result.pages.length === 0 ? (
                <Alert severity="info">No pages were returned (an empty response, or every page was skipped as empty).</Alert>
              ) : (
                result.pages.map((page, index) => (
                  <Accordion key={`${page.iteration}-${page.page}-${index}`} defaultExpanded={index === 0} disableGutters>
                    <AccordionSummary expandIcon={<ExpandMoreIcon />}>
                      <Stack direction="row" spacing={1} alignItems="center" sx={{ width: "100%" }}>
                        <Chip
                          size="small"
                          color={page.status >= 200 && page.status < 300 ? "success" : "warning"}
                          label={page.status}
                        />
                        <Mono sx={{ flexShrink: 0 }}>{page.method}</Mono>
                        <Mono sx={{ overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", flexGrow: 1 }}>
                          {page.url}
                        </Mono>
                        <Typography variant="caption" color="text.secondary" sx={{ flexShrink: 0 }}>
                          {page.durationMs} ms · {page.bytes} B · {page.recordCount < 0 ? "?" : page.recordCount} rec
                        </Typography>
                      </Stack>
                    </AccordionSummary>
                    <AccordionDetails>
                      <Stack spacing={1.5}>
                        {page.wouldLandTo && (
                          <Typography variant="body2" color="text.secondary">
                            Would land to <Mono>{page.wouldLandTo}</Mono>
                          </Typography>
                        )}
                        <HeaderTable title="Request headers" headers={page.requestHeaders} />
                        <HeaderTable title="Response headers" headers={page.responseHeaders} />
                        <Box>
                          <Typography variant="subtitle2" gutterBottom>
                            Response body {page.contentType ? `(${page.contentType})` : ""}
                          </Typography>
                          <CodeView value={page.bodyPreview} language={looksJson(page) ? "json" : "yaml"} height={280} />
                        </Box>
                      </Stack>
                    </AccordionDetails>
                  </Accordion>
                ))
              )}
            </Stack>
          )}
        </Stack>
      </DialogContent>
    </Dialog>
  );
}

function HeaderTable({ title, headers }: { title: string; headers: Record<string, string> }) {
  const entries = Object.entries(headers);
  if (entries.length === 0) {
    return null;
  }

  return (
    <Box>
      <Typography variant="subtitle2" gutterBottom>
        {title}
      </Typography>
      <Stack spacing={0.25}>
        {entries.map(([name, value]) => (
          <Typography key={name} variant="caption" component="div">
            <Mono>{name}</Mono>: {value}
          </Typography>
        ))}
      </Stack>
    </Box>
  );
}

/** Debug an existing integration: load its stored YAML first, then open the debugger prefilled with the real
 * document (falling back to the starter template only if the catalog copy is unavailable). */
function DebugExistingButton({ integration, onOpen }: { integration: Integration; onOpen: (yaml: string) => void }) {
  const [loading, setLoading] = useState(false);

  const openWithDefinition = async () => {
    setLoading(true);
    try {
      const detail = await pipelineApi.getById(integration.id);
      onOpen(detail.yaml || STARTER_YAML);
    } catch {
      onOpen(STARTER_YAML);
    } finally {
      setLoading(false);
    }
  };

  return (
    <Button
      size="small"
      startIcon={loading ? <CircularProgress size={14} /> : <BugReportIcon />}
      disabled={loading}
      onClick={() => void openWithDefinition()}
    >
      Debug
    </Button>
  );
}

export default function IntegrationsPage() {
  const [debuggerYaml, setDebuggerYaml] = useState<string | null>(null);
  const [codeFor, setCodeFor] = useState<Integration | null>(null);

  const flows = useQuery({ queryKey: ["integrations", "list"], queryFn: integrationApi.list });

  const columns = useMemo<Column<Integration>[]>(
    () => [
      { id: "name", header: "Integration", render: (f) => <Mono>{f.name}</Mono> },
      { id: "batch", header: "Batch", render: (f) => f.batch ?? "—" },
      { id: "path", header: "File", render: (f) => <Mono>{f.relativePath}</Mono> },
      {
        id: "active",
        header: "Status",
        render: (f) => <Chip size="small" label={f.active ? "active" : "inactive"} color={f.active ? "success" : "default"} />,
      },
      {
        id: "actions",
        header: "",
        align: "right",
        render: (f) => (
          <Stack direction="row" spacing={0.5} justifyContent="flex-end">
            <Button size="small" startIcon={<CodeIcon />} onClick={() => setCodeFor(f)}>
              View code
            </Button>
            <DebugExistingButton integration={f} onOpen={(yaml) => setDebuggerYaml(yaml)} />
          </Stack>
        ),
      },
    ],
    [],
  );

  const rows = flows.data ?? [];

  return (
    <Page data-testid="integrations-page">
      <PageHeader
        title="Integrations"
        subtitle="Acquisition flows (flowType: acq) in your estate: HTTP APIs, SFTP drops, S3 buckets, and storage tables, each landing raw payloads to the lake. View the YAML, test and debug here, and run or schedule them like any pipeline."
        actions={
          <Stack direction="row" spacing={1}>
            <Button variant="outlined" startIcon={<RefreshIcon />} onClick={() => flows.refetch()}>
              Refresh
            </Button>
            <Button variant="contained" startIcon={<BugReportIcon />} onClick={() => setDebuggerYaml(STARTER_YAML)}>
              New / Debug
            </Button>
          </Stack>
        }
      />

      {flows.isError ? (
        <Alert severity="error">Could not load integrations.</Alert>
      ) : rows.length === 0 && !flows.isLoading ? (
        <EmptyState
          icon={<CableIcon />}
          title="No integrations yet"
          description="Add a flowType: acq document to your repo (see samples/acquire), or click New / Debug to try one against a public API."
          action={
            <Button variant="contained" startIcon={<BugReportIcon />} onClick={() => setDebuggerYaml(STARTER_YAML)}>
              New / Debug
            </Button>
          }
          data-testid="integrations-empty"
        />
      ) : (
        <DataTable
          columns={columns}
          rows={flows.isLoading ? undefined : rows}
          rowKey={(f) => f.id}
          emptyMessage="No integrations."
        />
      )}

      {codeFor !== null && (
        <CodeDialog
          integration={codeFor}
          onClose={() => setCodeFor(null)}
          onDebug={(yaml) => {
            setCodeFor(null);
            setDebuggerYaml(yaml);
          }}
        />
      )}
      {debuggerYaml !== null && <DebuggerDialog initialYaml={debuggerYaml} onClose={() => setDebuggerYaml(null)} />}
    </Page>
  );
}
