import { useQuery } from "@tanstack/react-query";
import { ArrowRight, Info } from "lucide-react";
import { Skeleton } from "@/components/ui/skeleton";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { CopyButton } from "@/components/CopyButton";
import { DataTable, type Column } from "@/components/DataTable";
import { DetailPair } from "@/components/DetailPair";
import { RelativeTime } from "@/components/RelativeTime";
import {
  deliveryApi, type DeliveryDimensionAttributeSpec, type DeliveryDimensionChange, type DeliveryDimensionKey, type DeliveryDimensionValueDetail,
} from "../../../api/delivery";
import { ProblemView } from "../TemplateSheet";
import { counted } from "../assertions/assertionFormat";
import { KeySearchCell } from "./DimensionKeys";
import { DimensionFilterView } from "./DimensionFilterSheet";
import { DimensionValueText } from "./DimensionValueText";
import { CHANGE_TEXT, keyTail } from "./dimensionFormat";

/**
 * The columns of a value's keys: each key under the name the dimension calls it by, cut short where it begins like its
 * neighbours, with the records holding it, the search finding exactly them, and when it arrived. The attributes are the
 * value's, listed above the keys.
 */
const keyColumns = (keyColumn: string): Column<DeliveryDimensionKey>[] => [
  {
    id: "key",
    header: keyColumn,
    fill: true,
    floor: 220,
    render: (row) => <DimensionValueText value={row.key} maxWidth="100%" tail={keyTail(row.key)} testId="dimension-value-key" />,
  },
  {
    id: "count",
    header: "Records",
    align: "right",
    render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.count.toLocaleString("en-US")}</span>,
  },
  { id: "search", header: "Search", align: "right", render: (row) => <KeySearchCell row={row} /> },
  {
    id: "since",
    header: "Arrived",
    render: (row) => <span className="text-[12px] text-muted-foreground"><RelativeTime value={row.firstSeenUtc} absolute={false} /></span>,
  },
];

/**
 * The value's attributes, a line each in the order the dimension declares them: every value its keys hold, with how many
 * of the key's records hold it for a collected attribute of a value that is one key, and how many keys hold it for a value
 * of several.
 */
function ValueAttributes({ attributes, detail }: { attributes: DeliveryDimensionAttributeSpec[]; detail: DeliveryDimensionValueDetail }) {
  const only = detail.value.keys === 1 && detail.keys.length === 1 ? detail.keys[0] : null;
  return (
    <dl className="grid grid-cols-[minmax(6rem,max-content)_minmax(0,1fr)] gap-x-4 gap-y-1.5 text-[13px]" data-testid="dimension-value-attributes">
      {attributes.map((attribute) => {
        const held = detail.value.attributes.filter((a) => a.name === attribute.name);
        return (
          <div key={attribute.name} className="contents">
            <dt className="text-muted-foreground">{attribute.name}</dt>
            <dd className="flex min-w-0 flex-wrap items-baseline gap-x-3 gap-y-0.5">
              {held.length === 0 && <span className="text-muted-foreground/60">-</span>}
              {held.map((a) => {
                const records = only?.attributes.find((k) => k.name === a.name && k.value === a.value)?.records ?? null;
                return (
                  <span key={a.value} className="inline-flex min-w-0 items-baseline gap-1">
                    <DimensionValueText value={a.value} maxWidth={320} />
                    {records !== null && <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{records.toLocaleString("en-US")}</span>}
                    {records === null && detail.value.keys > 1 && (
                      <span className="font-mono text-[11px] tabular-nums text-muted-foreground">{counted(a.keys, "key")}</span>
                    )}
                  </span>
                );
              })}
            </dd>
          </div>
        );
      })}
    </dl>
  );
}

/** A change to one of the value's keys, on one line: when, which build, the key, and where it went. */
function HistoryRow({ change, valueId }: { change: DeliveryDimensionChange; valueId: number }) {
  const left = change.fromValueId === valueId;
  return (
    <li className="grid grid-cols-[7rem_4.5rem_minmax(0,1fr)] items-baseline gap-x-2 py-1 text-[12.5px]" data-testid="dimension-value-history-row">
      <span className="text-muted-foreground"><RelativeTime value={change.changedUtc} absolute={false} /></span>
      <span className="font-mono text-[11px] text-muted-foreground">build #{change.buildId}</span>
      <span className="flex min-w-0 flex-wrap items-baseline gap-1.5">
        <DimensionValueText value={change.key} maxWidth={200} />
        <span className={left ? "text-warning" : "text-success"}>{CHANGE_TEXT[change.change].verb}</span>
        {change.change === "moved" && (
          <span className="inline-flex items-baseline gap-1 text-muted-foreground">
            {change.fromValue === null ? "no value" : <DimensionValueText value={change.fromValue} maxWidth={140} />}
            <ArrowRight className="size-3.5 self-center" />
            {change.toValue === null ? "no value" : <DimensionValueText value={change.toValue} maxWidth={140} />}
          </span>
        )}
      </span>
    </li>
  );
}

/**
 * One value, whole, in a sheet: the value to copy, its records and keys, when it arrived and whether a build still finds
 * it; every key it stands for, exactly as the index holds it, with its label, its count and the filter finding its records,
 * the most records first; the search that finds the value's records; and the changes that brought keys to it or took them
 * away.
 */
export function DimensionValueSheet({ dimensionId, keyColumn, labelled, attributes, valueId, onClose }: {
  dimensionId: number;
  /** What the dimension calls its key: the heading of the keys' column. */
  keyColumn: string;
  /** The dimension reads a label for its keys. */
  labelled: boolean;
  /** The attributes the dimension reads of its keys. */
  attributes: DeliveryDimensionAttributeSpec[];
  /** The value to show; null closes the sheet. */
  valueId: number | null;
  onClose: () => void;
}) {
  const detail = useQuery({
    queryKey: ["delivery", "dimensions", "value", dimensionId, valueId],
    queryFn: () => deliveryApi.dimensionValue(dimensionId, valueId!),
    enabled: valueId !== null,
  });
  const data = detail.data;

  return (
    <Sheet open={valueId !== null} onOpenChange={(open) => { if (!open) { onClose(); } }}>
      <SheetContent
        className="w-full gap-0 sm:max-w-2xl"
        onOpenAutoFocus={(event) => { event.preventDefault(); (event.currentTarget as HTMLElement).focus(); }}
        data-testid="dimension-value-sheet"
      >
        <SheetHeader className="border-b border-border">
          <SheetTitle className="flex min-w-0 items-center gap-2">
            {data === undefined
              ? <Skeleton className="h-5 w-40" />
              : (
                <>
                  <DimensionValueText value={data.value.value} strong maxWidth={440} className="text-[15px]" testId="dimension-value-title" />
                  <CopyButton iconOnly label="Copy the value" text={data.value.value} testId="dimension-value-copy-value" />
                </>
              )}
          </SheetTitle>
          <SheetDescription>
            {labelled
              ? "A value of the dimension: the name read from the records its keys name, and the keys (ids) that stand for it."
              : "A value of the dimension: one cleaned value, and the keys cleaning gathered into it."}
          </SheetDescription>
        </SheetHeader>
        <div className="flex flex-1 flex-col gap-5 overflow-y-auto p-4">
          {detail.isError && <ProblemView error={detail.error} testId="dimension-value-error" />}
          {data === undefined && !detail.isError && <Skeleton className="h-64 w-full" />}
          {data !== undefined && (
            <>
              <div className="grid gap-3 [grid-template-columns:repeat(auto-fill,minmax(140px,1fr))]">
                <DetailPair label="Records">
                  <span className="font-mono tabular-nums" data-testid="dimension-value-detail-records">
                    {data.value.recordsExact ? "" : "~"}{data.value.records.toLocaleString("en-US")}
                  </span>
                </DetailPair>
                <DetailPair label="Keys"><span className="font-mono tabular-nums">{data.value.keys.toLocaleString("en-US")}</span></DetailPair>
                <DetailPair label="Arrived"><RelativeTime value={data.value.firstSeenUtc} absolute={false} /></DetailPair>
                <DetailPair label="Found now">
                  {data.value.removedUtc === null
                    ? <span>yes</span>
                    : <span className="text-warning">no, since <RelativeTime value={data.value.removedUtc} absolute={false} /></span>}
                </DetailPair>
              </div>

              {attributes.length > 0 && (
                <section className="flex flex-col gap-2">
                  <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Attributes</h3>
                  <ValueAttributes attributes={attributes} detail={data} />
                </section>
              )}

              <section className="flex flex-col gap-2">
                <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Keys</h3>
                <DataTable
                  columns={keyColumns(keyColumn)}
                  rows={data.keys}
                  rowKey={(row) => row.keyId}
                  emptyMessage="No build finds a key of this value now."
                  skeletonRows={3}
                  data-testid="dimension-value-keys-table"
                />
                {data.moreKeys && (
                  <p className="flex items-center gap-1.5 text-[12px] text-muted-foreground">
                    <Info className="size-4 shrink-0" />
                    These are the {data.keys.length.toLocaleString("en-US")} most common of {counted(data.value.keys, "key")}; the Keys tab lists every one.
                  </p>
                )}
              </section>

              <section className="flex flex-col gap-2">
                <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Finding its records</h3>
                {data.filter !== null
                  ? <DimensionFilterView filter={data.filter} />
                  : <p className="text-[13px] text-muted-foreground" data-testid="dimension-value-filter-problem">{data.filterProblem}</p>}
              </section>

              <section className="flex flex-col gap-1">
                <h3 className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">History</h3>
                {data.history.length === 0
                  ? <p className="text-[12.5px] text-muted-foreground">No build has moved a key to it or away from it since it arrived.</p>
                  : (
                    <ul className="divide-y divide-border" data-testid="dimension-value-history">
                      {data.history.map((change) => <HistoryRow key={change.changeId} change={change} valueId={data.value.valueId} />)}
                    </ul>
                  )}
              </section>
            </>
          )}
        </div>
      </SheetContent>
    </Sheet>
  );
}
