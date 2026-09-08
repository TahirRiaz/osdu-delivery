import { useEffect, useState, type FormEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { Search } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { searchApi } from "../../api/endpoints";
import { isApiError } from "../../api/client";
import type { FlowHit } from "../../api/types";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { Mono } from "../../components/Mono";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { PagedTable, type Column } from "../../components/PagedTable";
import { TruncatedText } from "../../components/TruncatedText";

const flowColumns: Column<FlowHit>[] = [
  {
    id: "name",
    header: "Flow",
    render: (row) => <span className="font-mono text-[12px] font-medium">{row.name}</span>,
  },
  { id: "kind", header: "Kind", render: (row) => <Badge variant="secondary">{row.kind}</Badge> },
  { id: "batch", header: "Batch", render: (row) => <Mono>{row.batch ?? "-"}</Mono> },
  { id: "repo", header: "Repo", render: (row) => <Mono>{row.repoName}</Mono> },
  { id: "matchedIn", header: "Matched", render: (row) => <Badge variant="outline">{row.matchedIn}</Badge> },
  {
    id: "path",
    header: "Path",
    render: (row) => <TruncatedText text={row.relativePath} mono maxWidth={360} />,
  },
  {
    id: "snippet",
    header: "Snippet",
    render: (row) => <Mono className="whitespace-pre-wrap">{row.snippet}</Mono>,
  },
];

/**
 * Search over the flow documents of every synced repository: by name, path, or body text. A multi-word term is
 * matched word by word (every word must match), which the summary line explains so a surprising miss is readable
 * without knowing the rule. The title-bar search box lands here with ?q=; the table pages through every hit.
 */
export default function SearchPage() {
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const q = (searchParams.get("q") ?? "").trim();
  const [term, setTerm] = useState(q);

  // The title-bar search navigates here while this page is already mounted: mirror the new term into the input.
  useEffect(() => {
    setTerm(q);
  }, [q]);

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = term.trim();
    setSearchParams(trimmed === "" ? {} : { q: trimmed }, { replace: true });
  };

  return (
    <Page data-testid="page-search">
      <PageHeader title="Search" />

      <form onSubmit={submit} className="flex items-center gap-2">
        <Input
          value={term}
          onChange={(event) => setTerm(event.target.value)}
          placeholder="Search term"
          aria-label="Search term"
          data-testid="search-input"
          className="h-8 max-w-[480px] flex-1"
        />
        <Button type="submit" size="sm" data-testid="search-submit">
          <Search />
          Search
        </Button>
      </form>

      {q === "" && (
        <EmptyState
          icon={<Search />}
          title="Type a term to search the flow documents by name, path, or body text"
          data-testid="search-hint"
        />
      )}

      {q !== "" && (
        <>
          <SearchSummary q={q} />
          <PagedTable<FlowHit>
            queryKey={["search", "flows", q]}
            fetchPage={(page, pageSize) => searchApi.flows(q, { page, pageSize })}
            columns={flowColumns}
            rowKey={(row) => row.id}
            onRowClick={(row) => navigate(`/pipelines/${row.id}`)}
            emptyMessage={`No flows match "${q}".`}
            data-testid="search-flows-table"
          />
        </>
      )}
    </Page>
  );
}

/** The one-line summary above the table: how many flows match, and how a multi-word term was read. */
function SearchSummary({ q }: { q: string }) {
  const query = useQuery({
    queryKey: ["search", "all", q],
    queryFn: () => searchApi.all(q),
  });

  if (query.isError) {
    return isApiError(query.error)
      ? <CorrelationError error={query.error} />
      : <p className="text-[13px] text-destructive">{String(query.error)}</p>;
  }

  const data = query.data;
  if (data === undefined) {
    return <Skeleton className="h-4 w-72" data-testid="search-summary-loading" />;
  }

  const total = data.flows.total;
  const words = data.tokens.length > 1 ? `; every word must match: ${data.tokens.join(" + ")}` : "";
  return (
    <p className="text-[13px] text-muted-foreground" data-testid="search-summary">
      {`${total.toLocaleString()} flow${total === 1 ? "" : "s"} match "${q}"${words}`}
    </p>
  );
}
