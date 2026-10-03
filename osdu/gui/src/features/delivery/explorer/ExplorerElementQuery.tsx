import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { CircleAlert, Info, Loader2, Search, SearchCode } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { CopyButton } from "@/components/CopyButton";
import { elementQueries, type ExplorerElementAnswer, type ExplorerElementQuery } from "../../../api/explorer";
import { failureText } from "../answers";
import type { InspectorField } from "../OsduRecordInspector";

/** How each query is headed, by what it finds. */
const PURPOSES: Record<ExplorerElementQuery["purpose"], string> = {
  equal: "This value, whole",
  words: "These words",
  exists: "Holds any",
};

/** The value of a leaf as the request carries it: a text, a number, a boolean, or null. */
function valueOf(value: unknown): string | number | boolean | null {
  return typeof value === "string" || typeof value === "number" || typeof value === "boolean" ? value : null;
}

/**
 * Beside every value and section of a record in the explorer (osdu/docs/explorer.md, The query of an element): the Lucene
 * queries that find records by it, written by the control plane from how the saved template of the record's kind has the
 * platform index it (a nested list reached with nested(...), a text's whole value by its keyword), each said in words,
 * with why none finds it where none can. A query is copied as written or as a search request, or searched in the explorer.
 */
export function ElementQueryButton({ field, onSearch }: { field: InspectorField; onSearch: (kind: string, query: string) => void }) {
  const [open, setOpen] = useState(false);
  const record = field.trail[field.trail.length - 1]?.record ?? null;
  const kind = record !== null && typeof record.kind === "string" ? record.kind : null;
  const section = field.node.kind !== "leaf";
  const request = kind === null
    ? null
    : { kind, path: field.node.path, section, ...(section ? {} : { value: valueOf(field.node.value) }) };
  const answer = useQuery({
    queryKey: ["explorer", "element-queries", request],
    queryFn: () => elementQueries(request!),
    enabled: open && request !== null,
    staleTime: 10 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  if (kind === null) {
    return null;
  }

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <button
          type="button"
          className="inline-flex size-5 items-center justify-center rounded text-muted-foreground hover:bg-accent hover:text-foreground"
          aria-label={`How to search ${field.node.path}`}
          title="How to search for this"
          onClick={(event) => event.stopPropagation()}
          data-testid="explorer-element-query"
          data-path={field.node.path}
        >
          <SearchCode className="size-3.5" />
        </button>
      </PopoverTrigger>
      {/* A click inside stays inside: the button can sit in a row that opens on a click. */}
      <PopoverContent align="end" className="w-[min(560px,90vw)] p-0" onClick={(event) => event.stopPropagation()} data-testid="explorer-element-queries">
        <div className="flex flex-col gap-2 border-b px-3 py-2">
          <div className="flex min-w-0 items-center gap-2">
            <SearchCode className="size-4 shrink-0 text-muted-foreground" aria-hidden />
            <span className="min-w-0 truncate font-mono text-[12.5px]" title={field.node.path}>{field.node.path}</span>
          </div>
          {answer.isPending && (
            <span className="inline-flex items-center gap-1.5 text-[12px] text-muted-foreground">
              <Loader2 className="size-3.5 animate-spin" aria-hidden />
              Reading how the index holds it
            </span>
          )}
          {answer.isError && <p className="text-[12px] text-destructive" data-testid="explorer-element-failed">{failureText(answer.error)}</p>}
          {answer.data !== undefined && <Reading answer={answer.data} />}
        </div>
        {answer.data !== undefined && (
          <Queries
            answer={answer.data}
            onSearch={(query) => {
              setOpen(false);
              onSearch(kind, query);
            }}
          />
        )}
      </PopoverContent>
    </Popover>
  );
}

function Reading({ answer }: { answer: ExplorerElementAnswer }) {
  return (
    <div className="flex flex-col gap-1 text-[12px] leading-5">
      {answer.reading !== null && <p data-testid="explorer-element-reading">{answer.reading}</p>}
      {answer.problem !== null && (
        <p className="flex items-start gap-1.5 text-foreground" data-testid="explorer-element-problem">
          <CircleAlert className="mt-0.5 size-3.5 shrink-0 text-warning" aria-hidden />
          <span className="min-w-0 break-words">{answer.problem}</span>
        </p>
      )}
      <p className="text-[11.5px] text-muted-foreground" data-testid="explorer-element-template">
        {answer.template === null
          ? "No saved template describes this kind, so how it is indexed is read from the value."
          : `As the saved template ${answer.template.kind} has the platform index it.`}
      </p>
    </div>
  );
}

function Queries({ answer, onSearch }: { answer: ExplorerElementAnswer; onSearch: (query: string) => void }) {
  return (
    <div className="flex max-h-[60vh] flex-col gap-2 overflow-auto px-3 py-2">
      {answer.queries.map((query) => (
        <section key={query.purpose} className="flex flex-col gap-1 rounded-md border px-2.5 py-2" data-testid="explorer-element-query-item" data-purpose={query.purpose}>
          <div className="flex min-w-0 items-center gap-2">
            <span className="text-[12px] font-medium">{PURPOSES[query.purpose]}</span>
            <span className="ml-auto flex shrink-0 items-center gap-0.5">
              <CopyButton iconOnly label="Copy the query" text={query.query} testId="explorer-element-copy" />
              <CopyButton
                label="As a request"
                text={JSON.stringify({ kind: answer.kind, query: query.query, limit: 10 }, null, 2)}
                testId="explorer-element-copy-request"
              />
              <Button variant="outline" size="sm" className="h-6 px-2 text-[12px]" onClick={() => onSearch(query.query)} data-testid="explorer-element-search">
                <Search />
                Search
              </Button>
            </span>
          </div>
          <code className="block break-all rounded bg-muted px-2 py-1 font-mono text-[12px]" data-testid="explorer-element-lucene">{query.query}</code>
          <p className="text-[11.5px] leading-4 text-muted-foreground">{query.says}</p>
        </section>
      ))}
      {answer.notes.length > 0 && (
        <ul className="flex flex-col gap-1" data-testid="explorer-element-notes">
          {answer.notes.map((note) => (
            <li key={note} className="flex items-start gap-1.5 text-[11.5px] leading-4 text-muted-foreground">
              <Info className="mt-0.5 size-3 shrink-0" aria-hidden />
              <span className="min-w-0 break-words">{note}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
