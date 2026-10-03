import { useEffect, useMemo, useRef, type ReactNode } from "react";
import { useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Telescope, Unplug } from "lucide-react";
import { Card } from "@/components/ui/card";
import { ResizableHandle, ResizablePanel, ResizablePanelGroup } from "@/components/ui/resizable";
import { useAuth } from "@/auth/AuthContext";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { RichTooltip } from "@/components/RichTooltip";
import { explorerApi, type ExplorerFilter, type ExplorerSearchRequest, type ExplorerSort, type ExplorerTypes } from "../../../api/explorer";
import { isPartitionId, useActivePartition } from "../activePartition";
import { useWindowFit } from "../useWindowFit";
import { ExplorerProblem } from "./ExplorerProblem";
import { ExplorerRecord } from "./ExplorerRecord";
import { ExplorerResults } from "./ExplorerResults";
import { ExplorerRecent, ExplorerSearchBar } from "./ExplorerSearchBar";
import { ExplorerTypeRail } from "./ExplorerTypeRail";
import {
  explorerErrorText, filtersOf, filtersText, recordAt, scopeKind, scopeLabel, scopeOf, sortOf, useExplorerRead, type ExplorerScope, type RecentRecord,
} from "./explorerModel";

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
 * The explorer (osdu/docs/explorer.md): a browser of what an OSDU partition holds, read live from OSDU. It opens on every
 * record of the partition the title bar names, with the types they are of beside them; a type, a group or a kind narrows
 * them, a search box takes an id (which opens the record), the start of one, a name, any text or a Lucene query, and a
 * property's values group and narrow them further. A record opens in the record inspector, under the place it sits in, with
 * its versions, its links and the records that mention it. Everything is in the address (the search, the place, the values,
 * the order and the record open), so a link, Back and a refresh land on the same view; the records already read are kept,
 * so going back is immediate. Nothing here reads what the delivery system keeps.
 */
export default function ExplorerPage() {
  const [params, setParams] = useSearchParams();
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

  // The types are counted for the search and its values, never for the place: the list is what a place is picked from.
  const typesRequest: ExplorerSearchRequest = { text: text === "" ? undefined : text, lucene, filters };
  const types = useExplorerRead<ExplorerTypes>(["types", active, typesRequest], reachable ? () => explorerApi.types(active, typesRequest) : null);
  const request = { text: text === "" ? undefined : text, lucene, kind: scopeKind(scope), filters, sort };


  const goScope = (next: ExplorerScope) => navigate({ kind: scopeKind(next) ?? null, id: null, v: null });
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
        key={`${text}|${lucene ? 1 : 0}`}
        text={text}
        lucene={lucene}
        placeholder={scope.level === "all" ? "Search by id, name or any text" : `Search ${scopeLabel(scope)} by id, name or any text`}
        onSearch={(typed, asLucene) => navigate({ q: typed, lq: asLucene ? "1" : null, id: null, v: null })}
        onOpenId={openId}
        className="min-w-[280px] max-w-[760px] flex-1"
      />
      <ExplorerRecent onOpen={openRecent} />
    </div>
  );

  let content;
  if (!canOperate) {
    content = (
      <EmptyState
        icon={<Telescope />}
        title="The explorer reads OSDU on a node"
        description="Each read runs on a node through a delivery flow's connection to the partition, which takes the operate scope. Ask an administrator for it."
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
        {/* The records stay as they were left while a record is open, so going back finds the list where it was. */}
        <WindowFrame hidden={recordId !== null} testId="explorer-browse-frame">
        <Card className="min-h-0 flex-1 gap-0 overflow-hidden rounded-lg p-0" data-testid="explorer-browse">
          <ResizablePanelGroup orientation="horizontal" className="min-h-0 flex-1">
            <ResizablePanel defaultSize={264} minSize={200} maxSize="40" className="flex min-h-0 flex-col border-r">
              <ExplorerTypeRail
                types={types.data?.answer}
                loading={types.isFetching}
                error={types.isError ? explorerErrorText(types.error) : types.data?.answer.refusal ?? null}
                scope={scope}
                onScope={goScope}
              />
            </ResizablePanel>
            <ResizableHandle />
            <ResizablePanel className="flex min-h-0 flex-col">
              <ExplorerResults
                partition={active}
                request={request}
                scope={scope}
                onScope={goScope}
                onOpen={(hit) => openId(hit.id)}
                onFilters={(next: ExplorerFilter[]) => navigate({ f: filtersText(next) })}
                onSort={(next: ExplorerSort) => navigate({ sort: next === "relevance" ? null : next }, true)}
                onSearchEverywhere={() => navigate({ kind: null })}
              />
            </ResizablePanel>
          </ResizablePanelGroup>
        </Card>
        </WindowFrame>
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
            />
          </WindowFrame>
        )}
      </>
    );
  }

  return (
    <Page data-testid="page-delivery-explorer">
      {header}
      {content}
    </Page>
  );
}
