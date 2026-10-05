import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Layers, TableProperties, Telescope, Unplug } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { useAuth } from "@/auth/AuthContext";
import { useSideBarFold } from "@/layout/workbench/SideBarRoom";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { RichTooltip } from "@/components/RichTooltip";
import type { DeliveryOsduRead } from "../../../api/delivery";
import { explorerApi, type ExplorerFilter, type ExplorerSearchRequest, type ExplorerSort, type ExplorerTypes } from "../../../api/explorer";
import { isPartitionId, useActivePartition } from "../activePartition";
import { useWindowFit } from "../useWindowFit";
import { failureText } from "../answers";
import { ElementQueryButton } from "./ExplorerElementQuery";
import { ExplorerProblem } from "./ExplorerProblem";
import { ExplorerRecord } from "./ExplorerRecord";
import { ExplorerResults } from "./ExplorerResults";
import { ExplorerRecent, ExplorerSearchBar } from "./ExplorerSearchBar";
import { ExplorerTypeRail } from "./ExplorerTypeRail";
import { ExplorerWelcome } from "./ExplorerWelcome";
import { BuildFieldActions } from "./dimension/BuildFieldActions";
import { DimensionBuildPanel } from "./dimension/DimensionBuildPanel";
import { useDimensionBuild } from "./dimension/useDimensionBuild";
import {
  ALL_KINDS, filtersOf, filtersText, recordAt, rememberType, scopeKind, scopeLabel, scopeOf, sortOf, useExplorerRead, type ExplorerScope, type RecentRecord,
} from "./explorerModel";

/** The kind a dimension built here reads from the explorer's place: a kind, or every version of a type; null for a group or every type. */
function buildKind(scope: ExplorerScope): string | null {
  return scope.level === "kind" || scope.level === "type" ? scopeKind(scope) ?? null : null;
}

/** The least height the explorer keeps, so a short window still shows a few rows. */
const MIN_HEIGHT = 420;

/** What stays under the explorer: the page's bottom padding and the workbench's status bar. */
const BELOW = 46;

/**
 * A frame as tall as the window leaves under where it starts, whatever it holds, so what is in it scrolls inside it and the
 * page does not. It fits itself when it mounts, and again when it is shown after being hidden.
 */
function WindowFrame({ hidden = false, children, testId }: { hidden?: boolean; children: ReactNode; testId: string }) {
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_HEIGHT, undefined, "height");
  return <div ref={frame} hidden={hidden} className="flex min-h-0 flex-col" data-testid={testId}>{children}</div>;
}

/**
 * The explorer (osdu/docs/explorer.md): a browser of what an OSDU partition holds, read live from OSDU. It opens on a
 * welcome that reads nothing from OSDU: the search box, the records and types opened lately in this browser, and the types
 * of the partition one click away. Once asked, the types of the partition the title bar names stand beside its records; a
 * type, a group or a kind narrows them, and a property's values group and narrow them further. The search box in the
 * header searches every type, and a type, a group or a kind picked has a search box of its own over its records, which
 * searches inside it; each takes an id (which opens the record), the start of one, a name, any text or a Lucene query,
 * and the search shows in the box whose place it searches. A record opens in the record inspector, under the place it sits in, with
 * its versions, its links and the records that mention it. Everything is in the address (the search, the place, the values,
 * the order and the record open), so a link, Back and a refresh land on the same view; the records already read are kept,
 * so going back is immediate. Nothing here reads what the delivery system keeps.
 */
export default function ExplorerPage() {
  const [params, setParams] = useSearchParams();
  const queryClient = useQueryClient();
  const [active, setActive] = useActivePartition();
  const { hasScope } = useAuth();
  const canOperate = hasScope("operate");

  // A link that names a partition makes it the title bar's, so the title bar says what is in view; the link has then done
  // its work, and the title bar decides from there on.
  const linked = params.get("partition");
  useEffect(() => {
    if (linked === null) {
      return;
    }

    if (isPartitionId(linked) && linked !== active) {
      setActive(linked);
    }

    setParams((current) => {
      const next = new URLSearchParams(current);
      next.delete("partition");
      return next;
    }, { replace: true });
  }, [linked, active, setActive, setParams]);

  const text = params.get("q") ?? "";
  const lucene = params.get("lq") === "1";
  const kindParam = params.get("kind");
  const scope = useMemo(() => scopeOf(kindParam), [kindParam]);
  const filterParam = params.get("f");
  const filters = useMemo(() => filtersOf(filterParam), [filterParam]);
  const sort = sortOf(params.get("sort"));
  const recordId = params.get("id");
  // What the reader asked for: records (a search, a place, values to narrow to), or the types alone to pick a place from.
  // Until then nothing is read from OSDU.
  const asksRecords = text !== "" || kindParam !== null || filters.length > 0;
  const browsing = asksRecords || params.get("view") === "types";
  // A group, a type or a kind picked: its records have a search box of their own, which searches inside it and shows the
  // search, while the header's searches every type.
  const picked = asksRecords && scope.level !== "all";
  // Edit puts the query sent in the field that shows the search, with the cursor at its end; each field is asked apart.
  const [editInHeader, setEditInHeader] = useState(0);
  const [editInPlace, setEditInPlace] = useState(0);
  const versionParam = Number(params.get("v") ?? "");
  const recordVersion = Number.isSafeInteger(versionParam) && versionParam > 0 ? versionParam : null;

  const navigate = (changes: Record<string, string | null>, replace = false) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null || value === "") {
        next.delete(key);
      } else {
        next.set(key, value);
      }
    }

    return next;
  }, { replace });

  const connection = useQuery({
    queryKey: ["explorer", "connection", active],
    queryFn: () => explorerApi.connection(active),
    staleTime: 60_000,
  });
  const reachable = canOperate && connection.data?.available === true;

  // A dimension built from what the explorer shows: the records are browsed and drilled into as ever, and each value picked
  // from where it is (osdu/docs/explorer.md, Building a dimension).
  const build = useDimensionBuild({ partition: active, reachable, scopeKind: buildKind(scope), recordId, recordVersion });
  const openKind = () => {
    const read = recordId === null ? undefined : queryClient.getQueryData<DeliveryOsduRead>(["explorer", "read", active, recordId, recordVersion]);
    return read?.found === true && typeof read.record?.kind === "string" ? read.record.kind : null;
  };
  const startKind = buildKind(scope) ?? build.recordKind ?? "";
  const building = build.draft !== null && connection.data?.available === true;
  // The workbench's side bar is folded while the builder is docked, so the records and the builder share the width.
  useSideBarFold(building);

  // The types are counted for the search and its values, never for the place: the list is what a place is picked from.
  const typesRequest: ExplorerSearchRequest = { text: text === "" ? undefined : text, lucene, filters };
  const types = useExplorerRead<ExplorerTypes>(
    ["types", active, typesRequest],
    reachable && browsing ? () => explorerApi.types(active, typesRequest) : null,
    // The kinds of a whole partition move slowly; those a search finds are read again sooner.
    typesRequest.text === undefined && filters.length === 0 ? 10 * 60_000 : undefined,
  );
  const request = { text: text === "" ? undefined : text, lucene, kind: scopeKind(scope), filters, sort };

  // Every type is a place picked like any other, so the address keeps it rather than falling back to the welcome.
  const goScope = (next: ExplorerScope) => {
    rememberType(next);
    navigate({ kind: scopeKind(next) ?? ALL_KINDS, view: null, id: null, v: null });
  };
  // An id typed with its version opens the record at that version; the version picker reads the others from there.
  const openId = (typed: string) => {
    const { id, version } = recordAt(typed);
    navigate({ id, v: version === null ? null : String(version) });
  };
  const openRecent = (record: RecentRecord) => {
    if (record.partition !== null && record.partition !== active) {
      setActive(record.partition);
    }

    navigate({ id: record.id, v: null });
  };

  const header = (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
      <h1 className="text-lg font-semibold leading-7">Explorer</h1>
      {connection.data?.available && (
        <RichTooltip
          title="Read live from OSDU"
          body={`Partition ${connection.data.partition ?? ""}, read through the connection of ${connection.data.through ?? "a delivery flow"} (${connection.data.endpoint ?? ""}). Every view is what OSDU's search and storage services answer now.`}
        >
          <span className="inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[12px] text-muted-foreground" data-testid="explorer-live">
            <span className="size-1.5 rounded-full bg-success" aria-hidden />
            {connection.data.partition}
          </span>
        </RichTooltip>
      )}
      <ExplorerSearchBar
        text={picked ? "" : text}
        lucene={picked ? false : lucene}
        placeholder={picked ? "Search every type by id, name or any text" : "Search by id, name or any text"}
        label="Search OSDU"
        onSearch={(typed, asLucene) => {
          if (typed === "") {
            // A search of every type cleared lists every record of every type; a place picked keeps its own search,
            // which its own field clears.
            if (!picked) {
              navigate({ q: null, lq: null, kind: kindParam ?? ALL_KINDS, id: null, v: null });
            }

            return;
          }

          navigate({ q: typed, lq: asLucene ? "1" : null, kind: picked ? ALL_KINDS : kindParam, id: null, v: null });
        }}
        onOpenId={openId}
        focusRequest={editInHeader}
        className="min-w-[280px] max-w-[760px] flex-1"
        testId="explorer-search"
      />
      {(browsing || recordId !== null) && <ExplorerRecent onOpen={openRecent} />}
      {reachable && build.draft === null && (
        <RichTooltip
          title="Build a dimension"
          body="Pick a dimension's key, value and attributes from the records as you browse them, following their links as far as a value is read, and get the YAML a dimension flow lists. The builder opens beside the records."
        >
          <Button variant="outline" size="sm" className="ml-auto" onClick={() => build.start(buildKind(scope) ?? openKind() ?? "")} data-testid="explorer-build-dimension">
            <TableProperties />
            Build a dimension
          </Button>
        </RichTooltip>
      )}
    </div>
  );

  let content;
  if (!canOperate) {
    content = (
      <EmptyState
        icon={<Telescope />}
        title="The explorer reads OSDU with a flow's credentials"
        description="Each read goes through a delivery flow's connection to the partition, which takes the operate scope. Ask an administrator for it."
        data-testid="explorer-no-scope"
      />
    );
  } else if (connection.isError) {
    content = <ExplorerProblem error={connection.error} />;
  } else if (connection.data !== undefined && !connection.data.available) {
    content = (
      <EmptyState
        icon={<Unplug />}
        title={connection.data.partition === null ? "Pick a partition" : `No connection to ${connection.data.partition}`}
        description={connection.data.reason ?? undefined}
        data-testid="explorer-no-connection"
      />
    );
  } else if (connection.data !== undefined) {
    content = (
      <>
        {!browsing && recordId === null && (
          <ExplorerWelcome
            partition={connection.data.partition}
            onBrowseTypes={() => navigate({ view: "types" })}
            onOpenRecent={openRecent}
            onOpenType={(type) => goScope(type.scope)}
          />
        )}
        {/* The records stay as they were left while a record is open, so going back finds the list where it was. */}
        {browsing && (
        <WindowFrame hidden={recordId !== null} testId="explorer-browse-frame">
        <Card className="min-h-0 flex-1 gap-0 overflow-hidden rounded-lg p-0" data-testid="explorer-browse">
          <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
            <ResizablePanel defaultSize={264} minSize={200} maxSize="40" className="flex min-h-0 flex-col border-r">
              <ExplorerTypeRail
                types={types.data?.answer}
                loading={types.isFetching}
                error={types.isError ? failureText(types.error) : types.data?.answer.refusal ?? null}
                scope={asksRecords ? scope : null}
                onScope={goScope}
              />
            </ResizablePanel>
            <ResizableHandle />
            <ResizablePanel className="flex min-h-0 flex-col">
              {/* Over the records of the place picked, in line with the filter over the types. */}
              {picked && (
                <div className="border-b p-2">
                  <ExplorerSearchBar
                    text={text}
                    lucene={lucene}
                    placeholder={`Search ${scopeLabel(scope)} by id, name or any text`}
                    label={`Search ${scopeLabel(scope)}`}
                    onSearch={(typed, asLucene) => navigate({ q: typed, lq: asLucene ? "1" : null, id: null, v: null })}
                    onOpenId={openId}
                    dense
                    focusRequest={editInPlace}
                    testId="explorer-within"
                  />
                </div>
              )}
              {asksRecords
                ? (
                  <ExplorerResults
                    partition={active}
                    request={request}
                    scope={scope}
                    onScope={goScope}
                    onOpen={(hit) => openId(hit.id)}
                    onFilters={(next: ExplorerFilter[]) => navigate({ f: filtersText(next) })}
                    onSort={(next: ExplorerSort) => navigate({ sort: next === "relevance" ? null : next }, true)}
                    onSearchEverywhere={() => navigate({ kind: ALL_KINDS })}
                    onEditQuery={(query) => {
                      if (picked) {
                        setEditInPlace((asked) => asked + 1);
                      } else {
                        setEditInHeader((asked) => asked + 1);
                      }

                      navigate({ q: query, lq: "1", f: null, id: null, v: null });
                    }}
                  />
                )
                : (
                  <EmptyState
                    icon={<Layers />}
                    title="Pick a type"
                    description="Pick a group, a type or a kind on the left, or search above. All types lists every record of the partition."
                    data-testid="explorer-pick-type"
                  />
                )}
            </ResizablePanel>
          </ResizablePanelGroup>
        </Card>
        </WindowFrame>
        )}
        {recordId !== null && (
          <WindowFrame testId="explorer-record-frame">
            <ExplorerRecord
              key={`${active ?? ""}|${recordId}|${recordVersion ?? ""}`}
              partition={active}
              id={recordId}
              version={recordVersion}
              onBack={() => navigate({ id: null, v: null })}
              onScope={goScope}
              onOpenId={openId}
              onBrowseQuery={(query) => navigate({ q: query, lq: "1", kind: null, f: null, id: null, v: null })}
              onSwitchPartition={(partition) => setActive(partition)}
              fieldActions={(field) => (
                <span className="inline-flex items-center gap-1">
                  <ElementQueryButton field={field} onSearch={(kind, query) => navigate({ q: query, lq: "1", kind, f: null, id: null, v: null })} />
                  {build.draft !== null && <BuildFieldActions field={field} build={build} />}
                </span>
              )}
            />
          </WindowFrame>
        )}
      </>
    );
  }

  // While a dimension is built, the builder docks beside whatever the explorer shows, in a frame of the same height.
  if (building) {
    content = (
      <WindowFrame testId="explorer-build-frame">
        <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
          <ResizablePanel minSize="40" className="flex min-h-0 flex-col gap-3 overflow-auto pr-1">
            {content}
          </ResizablePanel>
          <ResizableHandle />
          <ResizablePanel defaultSize={560} minSize={380} maxSize="60" className="flex min-h-0 flex-col pl-1">
            <Card className="flex min-h-0 flex-1 flex-col gap-0 overflow-hidden rounded-lg p-0">
              <DimensionBuildPanel build={build} startKind={startKind} />
            </Card>
          </ResizablePanel>
        </ResizablePanelGroup>
      </WindowFrame>
    );
  }

  return (
    <Page data-testid="page-delivery-explorer">
      {header}
      {content}
    </Page>
  );
}
