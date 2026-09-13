import { useState } from "react";
import { useSearchParams } from "react-router-dom";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ClipboardPaste, Cloud, FileJson, Trash2, type LucideIcon } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";
import { deliveryApi, type DeliveryTemplate } from "../../api/delivery";
import { useAuth } from "../../auth/AuthContext";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { DataTable, type Column } from "../../components/DataTable";
import { FilterBar } from "../../components/FilterBar";
import { GlyphRef } from "../../components/GlyphRef";
import { Page } from "../../components/Page";
import { PageHeader } from "../../components/PageHeader";
import { RelativeTime } from "../../components/RelativeTime";
import { SearchInput } from "../../components/SearchInput";
import { TruncatedText } from "../../components/TruncatedText";
import { KindText } from "./KindText";
import { TemplatesBrowseTab } from "./TemplatesBrowseTab";
import { TemplatesImportTab } from "./TemplatesImportTab";
import { ProblemView, problemText, TemplateSheet } from "./TemplateSheet";

type TemplatesTab = "saved" | "browse" | "import";

const TABS: readonly string[] = ["saved", "browse", "import"];

/**
 * Where a saved version came from, as the import and browse tabs word it, reduced to a glyph and a word so the column
 * stays narrow; the full origin is on hover. Null for an origin worded some other way, which then shows as text.
 */
function originVisual(origin: string): { icon: LucideIcon; label: string } | null {
  if (origin.startsWith("file ")) {
    return { icon: FileJson, label: "File" };
  }

  if (origin.startsWith("OSDU ")) {
    return { icon: Cloud, label: "OSDU" };
  }

  return origin === "pasted schema" ? { icon: ClipboardPaste, label: "Pasted" } : null;
}

function OriginCell({ origin }: { origin: string }) {
  const visual = originVisual(origin);
  return visual === null
    ? <TruncatedText text={origin} maxWidth={180} title="Origin" />
    : <GlyphRef icon={visual.icon} title="Origin" body={origin} label={visual.label} collapse />;
}

const savedColumns: Column<DeliveryTemplate>[] = [
  { id: "kind", header: "Kind", fill: true, floor: 150, render: (row) => <KindText kind={row.kind} /> },
  {
    id: "version",
    header: "Version",
    // In a narrow table eight characters of the content hash tell versions apart; the rest stays in the row's text.
    render: (row) => (
      <span className="font-mono text-[12px]">
        {row.version.slice(0, 8)}
        <span className="@max-3xl/table:sr-only">{row.version.slice(8)}</span>
      </span>
    ),
  },
  {
    id: "saved",
    header: "Saved",
    render: (row) => (
      <span className="inline-flex items-baseline gap-1.5">
        <RelativeTime value={row.capturedUtc} />
        {/* Who saved it gives way first in a narrow table; the template's sheet names them as well. */}
        <span className="inline-flex items-baseline gap-1.5 @max-3xl/table:sr-only">
          <span className="text-muted-foreground">by</span>
          <TruncatedText text={row.capturedBy} maxWidth={160} />
        </span>
      </span>
    ),
  },
  { id: "origin", header: "Origin", render: (row) => <OriginCell origin={row.origin} /> },
  {
    id: "pinned",
    header: "Pinned by",
    align: "right",
    render: (row) => (
      <span className={cn("font-mono tabular-nums", row.pinnedBy === 0 && "text-muted-foreground")}>
        {row.pinnedBy}
        <span className="@max-3xl/table:sr-only">{` mapping${row.pinnedBy === 1 ? "" : "s"}`}</span>
      </span>
    ),
  },
];

/**
 * Templates: the OSDU record schemas the catalog holds as templates, which mappings pin. A saved version is its content
 * and never changes. New versions come from the OSDU data definitions (the Open Group's public repository of OSDU schemas)
 * or, for a schema of one's own, from a bundled schema file, and each is looked at, variable by variable, before it is saved.
 */
export default function TemplatesPage() {
  const queryClient = useQueryClient();
  const { hasScope } = useAuth();
  const canAuthor = hasScope("author");
  const [searchParams, setSearchParams] = useSearchParams();
  const [storedTab, setTab] = useLocalStorageState<string>("sqlflow.templates.tab", "saved");
  const tab: TemplatesTab = TABS.includes(storedTab) ? (storedTab as TemplatesTab) : "saved";
  const [search, setSearch] = useState("");
  const [selected, setSelected] = useState<{ kind: string; version: string } | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);

  // A link to one saved version (/delivery/templates?kind=...&version=...) opens it, once per link.
  const linkedKind = searchParams.get("kind");
  const linkedVersion = searchParams.get("version");
  const link = linkedKind !== null && linkedVersion !== null ? `${linkedKind} ${linkedVersion}` : null;
  const [openedLink, setOpenedLink] = useState<string | null>(null);
  if (link !== openedLink) {
    setOpenedLink(link);
    if (linkedKind !== null && linkedVersion !== null) {
      setSelected({ kind: linkedKind, version: linkedVersion });
      setTab("saved");
    }
  }

  const templates = useQuery({ queryKey: ["delivery", "templates", "list"], queryFn: deliveryApi.templates });
  const detail = useQuery({
    queryKey: ["delivery", "templates", "detail", selected?.kind ?? null, selected?.version ?? null],
    queryFn: () => deliveryApi.templateDetail(selected!.kind, selected!.version),
    enabled: selected !== null,
  });

  const closeSheet = () => {
    setSelected(null);
    if (searchParams.has("kind") || searchParams.has("version")) {
      setSearchParams((current) => {
        const next = new URLSearchParams(current);
        next.delete("kind");
        next.delete("version");
        return next;
      }, { replace: true });
    }
  };

  const remove = useMutation({
    mutationFn: (template: { kind: string; version: string }) => deliveryApi.deleteTemplate(template.kind, template.version),
    onSuccess: (_result, template) => {
      setDeleteOpen(false);
      closeSheet();
      toast.success(`Deleted template ${template.kind} version ${template.version}.`);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
    onError: (error) => {
      // A 409 names the synced mappings that still pin the version; the sheet keeps the problem in view too.
      setDeleteOpen(false);
      toast.error(problemText(error));
    },
  });

  const term = search.trim().toLowerCase();
  const rows = templates.data === undefined
    ? undefined
    : templates.data
      .filter((template) => term === ""
        || template.kind.toLowerCase().includes(term)
        || template.version.toLowerCase().includes(term)
        || template.origin.toLowerCase().includes(term)
        || template.capturedBy.toLowerCase().includes(term))
      .sort((a, b) => a.kind.localeCompare(b.kind) || b.capturedUtc.localeCompare(a.capturedUtc));

  return (
    <Page data-testid="page-delivery-templates">
      <PageHeader
        title="Templates"
        subtitle="OSDU record schemas saved as templates. Every property becomes a variable a mapping can fill, and a saved version never changes."
      />
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList data-testid="templates-tabs">
          <TabsTrigger value="saved" data-testid="templates-tab-saved">Saved</TabsTrigger>
          <TabsTrigger value="browse" data-testid="templates-tab-browse">Browse OSDU</TabsTrigger>
          <TabsTrigger value="import" data-testid="templates-tab-import">Import file</TabsTrigger>
        </TabsList>
        {/* The tabs stay mounted so a search, its results and a pasted schema survive switching between them. */}
        <TabsContent value="saved" forceMount className="flex flex-col gap-3 data-[state=inactive]:hidden">
          <FilterBar>
            <SearchInput
              value={search}
              onChange={setSearch}
              placeholder="Kind, version, origin or who saved it"
              label="Filter the saved templates"
              testId="templates-saved-search"
            />
          </FilterBar>
          {templates.isError && <ProblemView error={templates.error} testId="templates-saved-error" />}
          {!templates.isError && (
            <DataTable
              columns={savedColumns}
              rows={rows}
              rowKey={(row) => `${row.kind} ${row.version}`}
              onRowClick={(row) => {
                remove.reset();
                setSelected({ kind: row.kind, version: row.version });
              }}
              emptyMessage={templates.data !== undefined && templates.data.length === 0
                ? "No template is saved yet. Browse OSDU or import a schema file to save one."
                : "No saved template matches the filter."}
              data-testid="templates-saved-table"
            />
          )}
        </TabsContent>
        <TabsContent value="browse" forceMount className="data-[state=inactive]:hidden">
          <TemplatesBrowseTab canAuthor={canAuthor} />
        </TabsContent>
        <TabsContent value="import" forceMount className="data-[state=inactive]:hidden">
          <TemplatesImportTab canAuthor={canAuthor} />
        </TabsContent>
      </Tabs>

      <TemplateSheet
        open={selected !== null}
        onClose={closeSheet}
        title={selected?.kind ?? "Template"}
        description={selected !== null ? `Saved version ${selected.version}, as the catalog holds it.` : ""}
        progress={null}
        problem={detail.isError
          ? <ProblemView error={detail.error} testId="templates-detail-error" />
          : remove.isError ? <ProblemView error={remove.error} testId="templates-delete-error" /> : null}
        detail={detail.data}
        actions={canAuthor && detail.data !== undefined ? (
          <Button
            variant="outline"
            size="sm"
            className="border-destructive/40 text-destructive hover:bg-destructive/10 hover:text-destructive"
            onClick={() => setDeleteOpen(true)}
            disabled={remove.isPending}
            data-testid="templates-delete"
          >
            <Trash2 />
            Delete
          </Button>
        ) : undefined}
        busy={remove.isPending}
        testId="templates-saved-sheet"
      />
      <ConfirmDialog
        open={deleteOpen && selected !== null}
        title="Delete this template version"
        message={selected === null
          ? ""
          : `Delete ${selected.kind} version ${selected.version}? No mapping can pin it afterwards. While a synced mapping pins it, the delete is refused.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (selected !== null) {
            remove.mutate(selected);
          }
        }}
        onClose={() => setDeleteOpen(false)}
      />
    </Page>
  );
}
