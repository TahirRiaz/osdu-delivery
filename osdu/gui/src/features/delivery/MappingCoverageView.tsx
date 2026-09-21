import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { OctagonAlert, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import { deliveryApi } from "../../api/delivery";
import { MappingPropertiesView } from "./MappingPropertiesView";
import { TemplateVariableExplorer } from "./TemplateVariableExplorer";
import { ProblemView } from "./TemplateSheet";
import { useParsedMapping } from "./useParsedMapping";
import { variableRows } from "./variableRows";

interface MappingCoverageViewProps {
  /** The mapping document as the catalog holds it. */
  yaml: string;
  /** Its path in the repository, which the messages name. */
  path: string;
  /** The document's content hash, so a mapping synced with new content is measured again. */
  contentHash: string;
}

/**
 * The template a mapping pins with the mapping laid over it: the record's tree, each variable saying what the document
 * fills it with and whether that reaches every row, and the required ones it leaves empty marked where they sit. It
 * opens on what the mapping fills and what a check names, which is what an author asks of a mapping, at a glance.
 * Nothing is rendered and no cache is read; this is the document against the schema alone.
 */
export function MappingCoverageView({ yaml, path, contentHash }: MappingCoverageViewProps) {
  const coverage = useQuery({
    queryKey: ["delivery", "mapping-coverage", path, contentHash],
    queryFn: () => deliveryApi.mappingCoverage(yaml, path),
  });
  const parsed = useParsedMapping(yaml, path, contentHash);

  const kind = coverage.data?.kind ?? null;
  const version = coverage.data?.version ?? null;
  const template = useQuery({
    queryKey: ["delivery", "template-detail", kind, version],
    queryFn: () => deliveryApi.templateDetail(kind!, version!),
    enabled: kind !== null && version !== null && (coverage.data?.variables.length ?? 0) > 0,
  });

  // A finding on a variable is shown on the variable itself; what is left here is about the document as a whole.
  const issues = (coverage.data?.issues ?? []).filter((issue) => issue.target === null);

  // An entry the template does not let a mapping fill sits on no row, so it is named here instead of going unseen.
  const outside = useMemo(() => {
    const draft = parsed.data?.draft ?? null;
    return template.data === undefined || draft === null
      ? []
      : variableRows(template.data, draft).filter((row) => row.kind === "outside");
  }, [template.data, parsed.data]);

  const overlay = coverage.data !== undefined && parsed.data?.draft != null && coverage.data.variables.length > 0
    ? { coverage: coverage.data, entries: parsed.data.draft.entries }
    : undefined;

  return (
    <div className="flex min-h-0 flex-1 flex-col gap-3" data-testid="delivery-mapping-coverage">
      {coverage.isError && <ProblemView error={coverage.error} testId="delivery-mapping-coverage-error" />}
      {template.isError && <ProblemView error={template.error} testId="delivery-mapping-coverage-template-error" />}
      {issues.map((issue, index) => (
        <p
          key={`${index}-${issue.message}`}
          className={cn("flex items-start gap-1.5 text-[13px]", issue.severity === "error" ? "text-destructive" : "text-warning")}
          data-testid="delivery-mapping-coverage-issue"
        >
          {issue.severity === "error"
            ? <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
            : <TriangleAlert className="mt-0.5 size-3.5 shrink-0" />}
          {issue.message}
        </p>
      ))}
      {outside.map((row) => (
        <p key={row.key} className="flex items-start gap-1.5 text-[13px] text-destructive" data-testid="delivery-mapping-coverage-outside">
          <OctagonAlert className="mt-0.5 size-3.5 shrink-0" />
          {row.outside}
        </p>
      ))}

      {overlay !== undefined && (
        <p className="text-xs text-muted-foreground" data-testid="delivery-mapping-coverage-note">
          What the mapping fills, and every required variable a check names. <strong className="font-medium">Show missing</strong>{" "}
          answers whether the mapping satisfies the schema: what this record requires and the mapping does not fill on
          every row. Show unfilled is the wider question, every variable of the template nothing fills, and Show
          everything is the template entire. A delivery is stopped only by a required property of{" "}
          <span className="font-mono">data</span> that nothing fills; the other findings are warnings, and the record is
          sent for OSDU to judge.
        </p>
      )}

      {template.data !== undefined && overlay !== undefined && (
        <TemplateVariableExplorer variables={template.data.variables} mapping={overlay} />
      )}

      {/*
        * Without a template to lay the mapping over, its own entries are still what a reader came for, so the document
        * is listed as it is written: a mapping pinning a version nobody saved is exactly when one wants to read it.
        */}
      {coverage.isSuccess && coverage.data.variables.length === 0 && (
        <MappingPropertiesView yaml={yaml} path={path} contentHash={contentHash} />
      )}
    </div>
  );
}
