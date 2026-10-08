import { useMemo, type ReactNode } from "react";
import { Link } from "react-router-dom";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { Info } from "lucide-react";
import { DataTable, type Column } from "@/components/DataTable";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { TruncatedText } from "@/components/TruncatedText";
import { inventoryApi, type Inventory, type InventoryRecord } from "../../../api/inventories";
import { RecordName } from "../RecordName";
import { RecordRef } from "../DeliveryRefs";
import { ProblemView } from "../TemplateSheet";
import { DimensionGrid, GridFooter, GridViewMenu } from "../dimensions/DimensionGrid";
import { useHiddenColumns, type GridColumnChoice } from "../dimensions/dimensionGridState";
import { ExplainTip, FindingName } from "./InventoryBadges";
import { findingVisual } from "./inventoryFormat";

const PAGE = 200;

/** The columns a grid of one finding starts without. */
const AT_FIRST_FINDING: readonly string[] = ["created"];

/** The columns a grid of every id starts without. */
const AT_FIRST_EVERY_ID: readonly string[] = ["created", "since"];

/** Why an id has its finding: what the finding means, then what the reconcile said of this id. */
function whyText(record: InventoryRecord): string {
  const meaning = findingVisual(record.finding).hint;
  return record.detail === undefined || record.detail === "" ? meaning : `${record.detail}\n\n${meaning}`;
}

/**
 * The ledger that holds the id, which opens its records, with the record's status (or the state of the artifact it rests on)
 * beside it; the whole of what the ledger holds of it, versions included, on hover.
 */
function LedgerCell({ row }: { row: InventoryRecord }) {
  if (row.ledgerFlowId === undefined) {
    return <span className="text-[12px] text-muted-foreground">no ledger knows it</span>;
  }

  const name = row.ledger ?? row.ledgerFlowId;
  const state = row.ledgerStatus ?? (row.artifactState === undefined ? undefined : `artifact ${row.artifactState}`);
  const held = [
    `Ledger ${name}`,
    row.ledgerStatus === undefined ? undefined : `record ${row.ledgerStatus}${row.ledgerVersion === undefined ? ", no version" : ` at version ${row.ledgerVersion}`}`,
    row.artifactId === undefined ? undefined : `artifact ${row.artifactId}${row.artifactState === undefined ? "" : ` ${row.artifactState}`}`,
  ].filter((part): part is string => part !== undefined).join("\n");
  return (
    <RichTooltip title="What the ledger holds" body={held} mono>
      <span className="flex min-w-0 items-center gap-1.5 text-[12px]">
        <Link
          to={`/delivery/records?flow=${encodeURIComponent(row.ledgerFlowId)}`}
          className="min-w-0 max-w-[10rem] truncate font-mono text-primary hover:underline"
          onClick={(event) => event.stopPropagation()}
          data-testid="inventory-record-ledger"
        >
          {name}
        </Link>
        {state !== undefined && <span className="shrink-0 text-muted-foreground">{state}</span>}
      </span>
    </RichTooltip>
  );
}

/**
 * The ids of an inventory with one finding, or every id, as a grid that fits its panel and scrolls inside the page: each id
 * named as a reader knows it (the whole id on hover, to copy), its version, the ledger and record it rests on (which open the
 * ledger's records and the record), who created it and when it last changed, and since when it has its finding; why it has
 * it is on hover over the mark beside it, to copy. More ids are read as the grid is scrolled, a page at a time after the
 * last id read. Columns are left out under View.
 */
export function InventoryRecordsGrid({ inventory, finding, total, leading }: {
  inventory: Inventory;
  /** The finding whose ids are listed; null lists every id, each with its finding. */
  finding: string | null;
  /** How many ids the grid can show, as the counts say; null when unknown. */
  total: number | null;
  /** What leads the line above the grid (the findings to pick from); the grid's View menu ends it. */
  leading?: ReactNode;
}) {
  // Every id adds a Finding column, so that view starts without the time each id has had its finding too.
  const atFirst = finding === null ? AT_FIRST_EVERY_ID : AT_FIRST_FINDING;
  const [hidden, toggleColumn] = useHiddenColumns(`osdu.inventories.records.columns.${finding === null ? "all" : "finding"}`, atFirst);
  const pages = useInfiniteQuery({
    queryKey: ["delivery", "inventories", "records", inventory.partition, inventory.inventoryId, finding],
    queryFn: ({ pageParam }) => inventoryApi.records(inventory.partition, inventory.inventoryId, {
      finding: finding ?? undefined, after: pageParam, limit: PAGE,
    }),
    initialPageParam: undefined as number | undefined,
    getNextPageParam: (last) => last.next,
    placeholderData: keepPreviousData,
  });
  const rows = useMemo(() => pages.data?.pages.flatMap((page) => page.items), [pages.data]);
  const more = () => {
    if (pages.hasNextPage && !pages.isFetchingNextPage) {
      void pages.fetchNextPage();
    }
  };

  const choices: GridColumnChoice[] = [
    { id: "version", label: "Version" },
    { id: "ledger", label: "Ledger" },
    { id: "record", label: "Record" },
    { id: "created", label: "Created by" },
    { id: "changed", label: "Changed" },
    { id: "since", label: "Since" },
  ];
  const all: Column<InventoryRecord>[] = [
    {
      id: "id",
      header: "Id",
      fill: true,
      floor: 210,
      render: (row) => (
        <span className="flex min-w-0 items-center gap-1.5">
          <RecordName id={row.targetId} kind={row.kind} copy className="text-[12.5px]" testId="inventory-record-id" copyTestId="inventory-record-copy-id" />
          {row.goneUtc !== undefined && (
            <RichTooltip body="OSDU no longer serves it: the inventory's read did not list it.">
              <span className="shrink-0 rounded-sm border border-border px-1 text-[10.5px] text-muted-foreground">gone <RelativeTime value={row.goneUtc} absolute={false} /></span>
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "why",
      header: finding === null ? "Finding" : "",
      render: (row) => (
        <span className="inline-flex items-center gap-1.5">
          {finding === null && <FindingName finding={row.finding} testId="inventory-record-finding" />}
          <ExplainTip title={`Why ${findingVisual(row.finding).label.toLowerCase()}`} text={whyText(row)} testId="inventory-record-why">
            <button
              type="button"
              className="inline-flex rounded-sm text-muted-foreground outline-none hover:text-foreground focus-visible:text-foreground"
              aria-label="Why it has its finding"
              onClick={(event) => event.stopPropagation()}
              data-testid="inventory-record-why"
            >
              <Info className="size-3.5" />
            </button>
          </ExplainTip>
        </span>
      ),
    },
    {
      id: "version",
      header: "Version",
      align: "right",
      render: (row) => (row.version === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <span className="font-mono text-[12px] tabular-nums">{row.version}</span>),
    },
    { id: "ledger", header: "Ledger", render: (row) => <LedgerCell row={row} /> },
    {
      id: "record",
      header: "Record",
      render: (row) => (row.ledgerFlowId !== undefined && row.deliveryKey !== undefined
        ? <RecordRef flowId={row.ledgerFlowId} deliveryKey={row.deliveryKey} />
        : <span className="text-[12px] text-muted-foreground">-</span>),
    },
    {
      id: "created",
      header: "Created by",
      render: (row) => (row.createUser === undefined
        ? <span className="text-[12px] text-muted-foreground">-</span>
        : <TruncatedText text={row.createUser} mono maxWidth={200} />),
    },
    {
      id: "changed",
      header: "Changed",
      render: (row) => {
        const at = row.modifyTime ?? row.createTime;
        if (at === undefined) {
          return <span className="text-[12px] text-muted-foreground">-</span>;
        }

        const by = row.modifyUser ?? row.createUser;
        const time = <span className="whitespace-nowrap text-[12px]"><RelativeTime value={at} absolute={false} /></span>;
        return by === undefined ? time : <RichTooltip title="Changed by" body={by} mono>{time}</RichTooltip>;
      },
    },
    {
      id: "since",
      header: "Since",
      render: (row) => (
        <RichTooltip body={`It has had this finding since this reconcile${row.firstSeenUtc === undefined ? "." : "; the inventory first listed it earlier or then."}`}>
          <span className="whitespace-nowrap text-[12px] text-muted-foreground"><RelativeTime value={row.findingUtc} absolute={false} /></span>
        </RichTooltip>
      ),
    },
  ];
  const columns = all.filter((column) => !hidden.has(column.id));
  const noun = finding === null ? "id" : `${findingVisual(finding).label.toLowerCase()} id`;

  return (
    <div className="flex min-w-0 flex-col gap-2" data-testid="inventory-records">
      <div className="flex min-w-0 flex-wrap items-center gap-1.5">
        {leading}
        <div className="ml-auto">
          <GridViewMenu columns={choices} hidden={hidden} atFirst={atFirst} onToggle={toggleColumn} testId="inventory-records-view" />
        </div>
      </div>
      {pages.isError
        ? <ProblemView error={pages.error} testId="inventory-records-error" />
        : (
          <DimensionGrid onNearEnd={more} testId="inventory-grid">
            <DataTable
              columns={columns}
              rows={rows}
              rowKey={(row) => row.inventoryRecordId}
              footer={(
                <GridFooter
                  shown={rows?.length ?? 0}
                  total={total}
                  noun={noun}
                  hasMore={pages.hasNextPage}
                  loading={pages.isFetchingNextPage}
                  onMore={more}
                  testId="inventory-records-footer"
                />
              )}
              emptyMessage={finding === null ? "The inventory holds no id yet." : `No id is ${findingVisual(finding).label.toLowerCase()}.`}
              data-testid="inventory-records-table"
            />
          </DimensionGrid>
        )}
    </div>
  );
}
