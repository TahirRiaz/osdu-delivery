import { useState } from "react";
import { Link as RouterLink } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { FileJson } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { isApiError } from "@/api/client";
import { deliveryApi, type DeliveryManualFlow } from "../../api/delivery";
import { CorrelationError } from "@/components/CorrelationError";
import { DataTable, type Column } from "@/components/DataTable";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { PageHeader } from "@/components/PageHeader";
import { SearchInput } from "@/components/SearchInput";
import { TruncatedText } from "@/components/TruncatedText";
import { KindText } from "./KindText";
import { SubmitRecordsDialog } from "./SubmitRecordsDialog";

/**
 * Manual submission: every flow whose document offers it (`source.manualSubmission`), with the mapping that renders
 * what is sent and the parameters a submission carries. Submitting from here makes the same
 * `POST /api/v1/delivery/submissions` a source system makes, so the records take the regular path: change detection,
 * the ledger, the drain and the record history. A flow that offers no manual submission is listed only on request,
 * with the reason, so it is clear why it cannot be picked.
 */
export default function ManualSubmissionPage() {
  const [search, setSearch] = useState("");
  const [showAll, setShowAll] = useState(false);
  const [chosen, setChosen] = useState<DeliveryManualFlow | null>(null);

  const flows = useQuery({
    queryKey: ["delivery", "manual-submission", showAll],
    queryFn: () => deliveryApi.manualSubmissionFlows(showAll),
  });

  const term = search.trim().toLowerCase();
  const rows = (flows.data ?? []).filter((flow) => term === ""
    || flow.flowName.toLowerCase().includes(term)
    || flow.mappingReference.toLowerCase().includes(term)
    || (flow.templateKind ?? "").toLowerCase().includes(term)
    || (flow.batch ?? "").toLowerCase().includes(term));

  const columns: Column<DeliveryManualFlow>[] = [
    {
      id: "flow",
      header: "Flow",
      render: (row) => (
        <div className="flex min-w-0 flex-col">
          <RouterLink to={`/pipelines/${row.pipelineId}`} className="truncate font-mono text-[13px] font-medium text-primary hover:underline">
            {row.flowName}
          </RouterLink>
          {row.batch !== null && <span className="truncate text-[11px] text-muted-foreground">{row.batch}</span>}
        </div>
      ),
    },
    {
      id: "mapping",
      header: "Renders with",
      fill: true,
      floor: 170,
      render: (row) => (
        <div className="flex min-w-0 flex-col items-start gap-0.5">
          <Badge variant="outline" className="font-mono text-[11px]">{row.mappingReference}</Badge>
          {row.templateKind !== null && (
            <span className="block w-full max-w-[260px]" data-testid={`manual-submission-template-${row.flowName}`}>
              <KindText kind={row.templateKind} />
            </span>
          )}
        </div>
      ),
    },
    {
      id: "protocol",
      header: "Protocol",
      // The payload a protocol carries is part of how the flow delivers, so it sits under the protocol.
      render: (row) => (
        <div className="flex flex-col items-start gap-0.5">
          <span className="font-mono text-[12px]">{row.protocol}</span>
          {row.payloadName !== null && <Badge variant="secondary" className="font-mono text-[11px]">{row.payloadName}</Badge>}
        </div>
      ),
    },
    {
      id: "parameters",
      header: "Parameters",
      render: (row) => (row.parameters.length === 0
        ? <span className="text-muted-foreground">-</span>
        : (
          <span className="inline-flex flex-wrap gap-1">
            {row.parameters.map((p) => (
              <Badge key={p.name} variant="secondary" className="font-mono text-[11px]">
                {p.name}{p.required && p.default === null ? "*" : ""}
              </Badge>
            ))}
          </span>
        )),
    },
    {
      id: "state",
      header: "",
      fill: true,
      floor: 120,
      render: (row) => (row.acceptsRecords
        ? (
          <Button size="sm" onClick={() => setChosen(row)} data-testid={`manual-submit-${row.flowName}`}>
            <FileJson />
            <span className="@max-3xl/table:sr-only">Submit records</span>
          </Button>
        )
        : <TruncatedText text={row.recordsRefusal} maxWidth={1200} />),
    },
  ];

  return (
    <Page data-testid="page-manual-submission">
      <PageHeader
        title="Manual submission"
        subtitle="Send records to a flow the way a source system does: the flow's mapping renders them and the run delivers them like any other submission."
      />
      {flows.isError && (isApiError(flows.error)
        ? <CorrelationError error={flows.error} />
        : <p className="text-[13px] text-destructive">{String(flows.error)}</p>)}
      <div className="flex flex-wrap items-center gap-3">
        <SearchInput
          value={search}
          onChange={setSearch}
          placeholder="Filter by flow, batch or mapping"
          label="Filter the flows"
          testId="manual-submission-search"
        />
        <Label className="flex items-center gap-2 text-[13px] font-normal">
          <Switch checked={showAll} onCheckedChange={setShowAll} data-testid="manual-submission-show-all" />
          Show flows that take no records, and why
        </Label>
      </div>
      {flows.data !== undefined && flows.data.length === 0 && !showAll ? (
        <EmptyState
          icon={<FileJson />}
          title="No flow offers manual submission"
          description="A flow takes records sent from here once its document declares source.manualSubmission. A flow that streams payload files takes them too: its records say where the files already sit, and the node reads them from there."
          data-testid="manual-submission-empty"
        />
      ) : (
        <DataTable
          columns={columns}
          rows={rows}
          rowKey={(row) => row.pipelineId}
          emptyMessage="No flow matches the filter."
          data-testid="manual-submission-flows"
        />
      )}
      {chosen !== null && (
        <SubmitRecordsDialog
          open
          onClose={() => setChosen(null)}
          pipelineId={chosen.pipelineId}
          flowName={chosen.flowName}
        />
      )}
    </Page>
  );
}
