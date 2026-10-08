import { useMemo, useRef, useState } from "react";
import { Link as RouterLink, useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { CircleOff, EyeOff, Info, Search, Telescope, TextSearch, Undo2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from "@/components/ui/sheet";
import { Switch } from "@/components/ui/switch";
import { Textarea } from "@/components/ui/textarea";
import { EmptyState } from "@/components/EmptyState";
import { Page } from "@/components/Page";
import { RelativeTime } from "@/components/RelativeTime";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { failureText } from "../answers";
import { ExplorerGrid, type GridColumn } from "../explorer/ExplorerGrid";
import { kindParts } from "../explorer/explorerModel";
import { routeVersions, termSource } from "../explorer/explorerTerms";
import { useWindowFit } from "../useWindowFit";
import { searchTermsApi, searchedRoute, type SearchRouteView, type SearchTermView } from "../../../api/searchTerms";

/** What the grid lists: every term, those searched, those left out, those that cannot be searched, or refinements of terms no longer found. */
type Show = "all" | "searched" | "excluded" | "unsearchable" | "orphans";

const SHOWS: { value: Show; label: string }[] = [
  { value: "all", label: "Every term" },
  { value: "searched", label: "Searched" },
  { value: "excluded", label: "Left out" },
  { value: "unsearchable", label: "Not searchable" },
  { value: "orphans", label: "No longer found" },
];

/** The least height the grid keeps, so a short window still shows a few rows. */
const MIN_HEIGHT = 360;

/** What stays under the grid: the page's bottom padding and the workbench's status bar. */
const BELOW = 46;

/** A term's state, in a word and a mark: searched, left out, not searchable, no longer found. */
function State({ term }: { term: SearchTermView }) {
  if (term.orphan) {
    return <span className="text-[12px] text-muted-foreground" title={term.problem ?? undefined}>No longer found</span>;
  }

  if (term.excluded) {
    return <span className="inline-flex items-center gap-1 text-[12px] text-muted-foreground"><EyeOff className="size-3.5" />Left out</span>;
  }

  if (term.problem) {
    return (
      <RichTooltip title="Not searchable" body={term.problem}>
        <span className="inline-flex items-center gap-1 text-[12px] text-muted-foreground"><CircleOff className="size-3.5 text-warning" />Not searchable</span>
      </RichTooltip>
    );
  }

  return <span className="inline-flex items-center gap-1 text-[12px]"><TextSearch className="size-3.5 text-success" />Searched</span>;
}

/** Where a route reaches the record, as a reader reads it: the path without the `data.` every content path starts with. */
function routeLabel(route: SearchRouteView | null): string {
  if (route === null) {
    return "";
  }

  return route.path === "id" ? "the record's id" : route.path.replace(/^data\./, "");
}

/** How a route carries the value, in a word beside its path; none for a copy or a key, whose path says it all. */
const ROUTE_WORDS: Record<SearchRouteView["kind"], string | null> = {
  copy: null,
  key: null,
  steps: "steps",
  lookup: "lookup",
  search: "search",
  expression: "expression",
};

/**
 * Search terms (osdu/docs/search-terms.md): the columns of the source systems that the mappings of active delivery flows
 * read, extracted by every repository sync, each with the routes by which it reaches the records. Here they are refined:
 * renamed to what the people searching call them, left out of the search, searched through another of their routes, or
 * given a note; what is made of a term holds across syncs and mapping versions. The explorer offers the terms searched,
 * by their names, wherever it offers a property of the type.
 */
export default function SearchTermsPage() {
  const [params, setParams] = useSearchParams();
  const queryClient = useQueryClient();
  const entityType = params.get("type");
  const show = (SHOWS.find((s) => s.value === params.get("show"))?.value ?? "all") as Show;
  const [typed, setTyped] = useState("");
  const [open, setOpen] = useState<string | null>(null);
  const frame = useRef<HTMLDivElement>(null);
  useWindowFit(frame, BELOW, MIN_HEIGHT, undefined, "height");

  const types = useQuery({ queryKey: ["delivery", "search-terms", "types"], queryFn: () => searchTermsApi.entityTypes(), staleTime: 30_000 });
  const chosen = entityType ?? types.data?.[0]?.entityType ?? null;
  const terms = useQuery({
    queryKey: ["delivery", "search-terms", "list", chosen],
    queryFn: () => searchTermsApi.list({ entityType: chosen!, orphans: true }),
    enabled: chosen !== null,
    staleTime: 30_000,
  });

  const shown = useMemo(() => {
    const wanted = typed.trim().toLowerCase();
    return (terms.data?.terms ?? [])
      .filter((term) => {
        switch (show) {
          case "searched":
            return !term.orphan && !term.excluded && term.problem === null;
          case "excluded":
            return term.excluded && !term.orphan;
          case "unsearchable":
            return !term.orphan && term.problem !== null;
          case "orphans":
            return term.orphan;
          default:
            return !term.orphan;
        }
      })
      .filter((term) => wanted === "" || [term.name, term.columnLabel, term.source, ...term.routes.map((r) => r.path)].some((text) => text.toLowerCase().includes(wanted)));
  }, [terms.data, show, typed]);
  const counts = useMemo(() => {
    const all = terms.data?.terms ?? [];
    return {
      all: all.filter((t) => !t.orphan).length,
      searched: all.filter((t) => !t.orphan && !t.excluded && t.problem === null).length,
      excluded: all.filter((t) => t.excluded && !t.orphan).length,
      unsearchable: all.filter((t) => !t.orphan && t.problem !== null).length,
      orphans: all.filter((t) => t.orphan).length,
    } satisfies Record<Show, number>;
  }, [terms.data]);

  const navigate = (changes: Record<string, string | null>) => setParams((current) => {
    const next = new URLSearchParams(current);
    for (const [key, value] of Object.entries(changes)) {
      if (value === null) {
        next.delete(key);
      } else {
        next.set(key, value);
      }
    }

    return next;
  }, { replace: true });

  const columns: GridColumn<SearchTermView>[] = [
    {
      id: "name",
      header: "Name",
      flex: 1.6,
      render: (term) => (
        <span className="flex min-w-0 items-center gap-1.5">
          <span className={cn("min-w-0 truncate", term.renamed && "font-medium")} title={term.name}>{term.name}</span>
          {term.note && (
            <RichTooltip title="Note" body={term.note}>
              <Info className="size-3.5 shrink-0 text-muted-foreground" aria-label="Note" />
            </RichTooltip>
          )}
        </span>
      ),
    },
    {
      id: "column",
      header: "Source column",
      flex: 1.5,
      render: (term) => (
        // The table and the column the term is, in full; a long table's name cut from its start, so the column stays in view.
        <span dir="rtl" className="min-w-0 truncate text-left font-mono text-[12px]" title={termSource(term)}>
          <bdi dir="ltr">
            {term.source !== "" && <span className="text-muted-foreground">{term.source}.</span>}
            {term.column}
          </bdi>
        </span>
      ),
    },
    {
      id: "route",
      header: "Searched as",
      flex: 2,
      render: (term) => {
        const route = searchedRoute(term) ?? term.routes.find((r) => r.id === term.route) ?? null;
        return route === null
          ? <span className="text-[12px] text-muted-foreground">-</span>
          : (
            <span className="flex min-w-0 items-center gap-1.5" title={`${route.target}\n${route.how}${route.find ? `, ${route.find.name} by ${route.find.lines.map((l) => l.field.replace(/^data\./, "")).join(" or ")}` : ""}`}>
              <span className="min-w-0 truncate font-mono text-[12px]">{routeLabel(route)}</span>
              {ROUTE_WORDS[route.kind] !== null && <span className="shrink-0 rounded-sm border px-1 text-[10px] text-muted-foreground">{ROUTE_WORDS[route.kind]}</span>}
            </span>
          );
      },
    },
    { id: "state", header: "State", width: 132, render: (term) => <State term={term} /> },
    {
      id: "changed",
      header: "Changed",
      width: 150,
      align: "right",
      render: (term) => (term.updatedUtc
        ? <span className="min-w-0 truncate text-[12px] text-muted-foreground" title={term.updatedBy ?? undefined}><RelativeTime value={term.updatedUtc} absolute={false} /></span>
        : <span className="text-[12px] text-muted-foreground/60">as extracted</span>),
    },
  ];

  const selected = (terms.data?.terms ?? []).find((term) => term.id === open) ?? null;
  let body;
  if (types.isPending || (chosen !== null && terms.isPending)) {
    body = <p className="p-4 text-[13px] text-muted-foreground">Reading the search terms.</p>;
  } else if (types.isError || terms.isError) {
    body = <p className="p-4 text-[13px] text-destructive">{failureText(types.error ?? terms.error)}</p>;
  } else if (chosen === null) {
    body = (
      <EmptyState
        icon={<TextSearch />}
        title="No search terms yet"
        description="Search terms are extracted from the mappings of active delivery flows each time a repository syncs. Sync a repository whose delivery flows pin a mapping."
        data-testid="search-terms-empty"
      />
    );
  } else if (shown.length === 0) {
    body = <EmptyState icon={<Search />} title="No term matches" description="Change the filter, or show every term." data-testid="search-terms-none" />;
  } else {
    body = (
      <ExplorerGrid
        rows={shown}
        columns={columns}
        rowKey={(term) => term.id}
        onOpen={(term) => setOpen(term.id)}
        label="Search terms"
        testId="search-terms-grid"
      />
    );
  }

  return (
    <Page data-testid="page-delivery-search-terms">
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        <h1 className="text-lg font-semibold leading-7">Search terms</h1>
        <RichTooltip
          title="Search terms"
          body="The columns of your source systems, as the mappings of active delivery flows read them, each with the place in the OSDU record it fills. The explorer searches by them: a value typed for a term is put through the mapping as a delivery puts it, and the search asks OSDU's own search service. Each sync extracts them again; a name, a note, leaving a term out or the route picked here holds across syncs."
        >
          <Info className="size-4 text-muted-foreground" aria-label="About search terms" />
        </RichTooltip>
        <Select value={chosen ?? ""} onValueChange={(next) => navigate({ type: next })}>
          <SelectTrigger size="sm" className="h-8 w-[300px] text-[13px]" data-testid="search-terms-type">
            <SelectValue placeholder="Pick a type" />
          </SelectTrigger>
          <SelectContent>
            {(types.data ?? []).map((type) => (
              <SelectItem key={type.entityType} value={type.entityType} className="text-[13px]">
                {kindParts(`*:*:${type.entityType}:*`).type}
                <span className="ml-2 text-[11px] text-muted-foreground">{type.terms}</span>
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className="relative min-w-[220px] max-w-[360px] flex-1">
          <Search className="pointer-events-none absolute left-2.5 top-2 size-4 text-muted-foreground" />
          <Input value={typed} onChange={(event) => setTyped(event.target.value)} placeholder="Find a term, a column or a path" className="h-8 pl-8 text-[13px]" data-testid="search-terms-find" />
        </div>
        {chosen !== null && (
          <Button asChild variant="outline" size="sm" className="ml-auto h-8 gap-1.5 text-[13px]">
            <RouterLink to={`/delivery/explorer?kind=${encodeURIComponent(`*:*:${chosen}:*`)}`} data-testid="search-terms-explore">
              <Telescope />
              Search {kindParts(`*:*:${chosen}:*`).type}
            </RouterLink>
          </Button>
        )}
      </div>
      <div ref={frame} className="flex min-h-0 flex-col" data-testid="search-terms-frame">
        <Card className="min-h-0 flex-1 gap-0 overflow-hidden rounded-lg p-0">
          <div className="flex items-center gap-1 border-b px-2 py-1.5" role="tablist" aria-label="Which terms">
            {SHOWS.map((option) => (
              <button
                key={option.value}
                type="button"
                role="tab"
                aria-selected={show === option.value}
                onClick={() => navigate({ show: option.value === "all" ? null : option.value })}
                className={cn("rounded-md px-2 py-1 text-[12px] text-muted-foreground hover:bg-accent/60 hover:text-foreground", show === option.value && "bg-accent text-foreground")}
                data-testid="search-terms-show"
              >
                {option.label}
                <span className="ml-1.5 font-mono tabular-nums text-[11px]">{counts[option.value]}</span>
              </button>
            ))}
          </div>
          {body}
        </Card>
      </div>
      <Sheet open={selected !== null} onOpenChange={(next) => { if (!next) { setOpen(null); } }}>
        <SheetContent className="w-[520px] gap-0 p-0 sm:max-w-[520px]" data-testid="search-term-sheet">
          {selected !== null && (
            <TermEditor
              key={`${selected.id}:${selected.updatedUtc ?? ""}`}
              term={selected}
              onSaved={() => void queryClient.invalidateQueries({ queryKey: ["delivery", "search-terms"] })}
            />
          )}
        </SheetContent>
      </Sheet>
    </Page>
  );
}

/**
 * One term refined: the name it is searched by (empty for the column's own), a note shown beside it, whether the explorer
 * offers it, and the route it is searched through among those that can be; with every route listed, how it reaches the
 * record and why one cannot be searched. Saving keeps it across syncs; reset gives the term back as its mappings give it.
 */
function TermEditor({ term, onSaved }: { term: SearchTermView; onSaved: () => void }) {
  const [name, setName] = useState(term.renamed ? term.name : "");
  const [note, setNote] = useState(term.note ?? "");
  const [excluded, setExcluded] = useState(term.excluded);
  const [picked, setPicked] = useState<string | null>(term.pickedRoute);
  const save = useMutation({
    mutationFn: () => searchTermsApi.refine(term.id, { name: name.trim() === "" ? null : name.trim(), note: note.trim() === "" ? null : note.trim(), excluded, route: picked }, term.entityType),
    onSuccess: onSaved,
  });
  const reset = useMutation({ mutationFn: () => searchTermsApi.reset(term.id), onSuccess: onSaved });
  const changed = (term.renamed ? term.name : "") !== name.trim() || (term.note ?? "") !== note.trim() || term.excluded !== excluded || term.pickedRoute !== picked;
  const refined = term.renamed || term.note !== null || term.excluded || term.pickedRoute !== null;
  const failure = save.error ?? reset.error;

  return (
    <div className="flex h-full min-h-0 flex-col">
      <SheetHeader className="border-b px-4 py-3">
        <SheetTitle className="text-base">{term.name}</SheetTitle>
        <SheetDescription className="font-mono text-[12px]">
          {termSource(term)} on {kindParts(`*:*:${term.entityType}:*`).type}
        </SheetDescription>
      </SheetHeader>
      <div className="flex min-h-0 flex-1 flex-col gap-4 overflow-y-auto px-4 py-3">
        {term.orphan
          ? <p className="text-[13px] text-muted-foreground" data-testid="search-term-orphan">No pipeline of an active delivery flow reads {term.columnLabel} any longer. What was made of it is kept until it is removed, and applies again if the column comes back.</p>
          : (
            <>
              <label className="flex flex-col gap-1 text-[12px] text-muted-foreground">
                Name
                <Input value={name} onChange={(event) => setName(event.target.value)} placeholder={term.columnLabel} maxLength={100} className="h-8 text-[13px] text-foreground" data-testid="search-term-name" />
              </label>
              <label className="flex flex-col gap-1 text-[12px] text-muted-foreground">
                Note
                <Textarea value={note} onChange={(event) => setNote(event.target.value)} placeholder="What the term means to the people searching by it" maxLength={1000} rows={2} className="text-[13px] text-foreground" data-testid="search-term-note" />
              </label>
              <label className="flex items-center justify-between gap-3 text-[13px]">
                <span className="flex flex-col">
                  Offered in the explorer
                  <span className="text-[11px] text-muted-foreground">Left out, the term is listed here and never offered as a search.</span>
                </span>
                <Switch checked={!excluded} onCheckedChange={(on) => setExcluded(!on)} data-testid="search-term-offered" />
              </label>
              <div className="flex flex-col gap-1.5">
                <span className="text-[12px] text-muted-foreground">Searched as</span>
                <div className="flex flex-col gap-1" role="radiogroup" aria-label="The route the term is searched through" data-testid="search-term-routes">
                  {term.routes.map((route) => {
                    const searched = route.id === (picked ?? (term.pickedRoute === null ? term.route : null));
                    return (
                      <button
                        key={route.id}
                        type="button"
                        role="radio"
                        aria-checked={searched}
                        disabled={route.problem !== null}
                        onClick={() => setPicked(route.id === term.route && term.pickedRoute === null ? null : route.id)}
                        className={cn(
                          "flex flex-col items-start gap-0.5 rounded-md border px-2.5 py-1.5 text-left text-[12px] disabled:cursor-not-allowed disabled:opacity-60",
                          searched ? "border-primary bg-primary/5" : "hover:bg-accent/50",
                        )}
                        title={route.problem ?? route.description ?? undefined}
                        data-testid="search-term-route"
                      >
                        <span className="flex w-full min-w-0 items-center gap-2">
                          <span className="min-w-0 flex-1 truncate font-mono">{routeLabel(route)}</span>
                          {route.index && <span className="shrink-0 text-[11px] text-muted-foreground">{route.index}{route.nested ? `, in ${route.nested.replace(/^data\./, "")}` : ""}</span>}
                        </span>
                        <span className="text-[11px] text-muted-foreground">
                          {route.how}
                          {route.steps.length > 0 ? `: ${route.steps.join(", ")}` : ""}
                          {route.find ? `, ${route.find.name} by ${route.find.lines.map((l) => l.field.replace(/^data\./, "")).join(" or ")}` : ""}
                        </span>
                        {/* Which versions of the type write the column this way: where they differ, each searches its own. */}
                        {route.kinds.length > 0 && (
                          <span className="text-[11px] text-muted-foreground" data-testid="search-term-route-versions">
                            {`${kindParts(route.kinds[0]).type} ${routeVersions(route.kinds)}`}
                          </span>
                        )}
                        {route.problem && <span className="text-[11px] text-warning">{route.problem}</span>}
                      </button>
                    );
                  })}
                </div>
              </div>
            </>
          )}
        <div className="flex flex-col gap-1 text-[12px]">
          <span className="text-muted-foreground">Read by</span>
          <span className="font-mono text-[11px]">{term.mappings.join(", ") || "-"}</span>
          <span className="font-mono text-[11px] text-muted-foreground">{term.flows.join(", ")}</span>
        </div>
        {term.updatedUtc && (
          <p className="text-[11px] text-muted-foreground">
            Changed by {term.updatedBy} <RelativeTime value={term.updatedUtc} absolute={false} />
          </p>
        )}
        {failure && <p className="text-[12px] text-destructive" data-testid="search-term-error">{failureText(failure)}</p>}
      </div>
      <div className="flex items-center gap-2 border-t px-4 py-2.5">
        {refined && (
          <Button variant="ghost" size="sm" className="gap-1.5" onClick={() => reset.mutate()} disabled={reset.isPending} data-testid="search-term-reset">
            <Undo2 />
            {term.orphan ? "Remove" : "Reset"}
          </Button>
        )}
        {!term.orphan && (
          <Button size="sm" className="ml-auto" onClick={() => save.mutate()} disabled={!changed || save.isPending} data-testid="search-term-save">
            Save
          </Button>
        )}
      </div>
    </div>
  );
}
