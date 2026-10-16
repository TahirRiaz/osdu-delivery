import { useMemo } from "react";
import { cn } from "@/lib/utils";
import type { DeliveryDimensionYaml, DimensionYamlSpan } from "../../../api/delivery";
import { YamlHeading } from "./BlueprintYaml";
import { declaredOf } from "./dimensionViewFormat";
import { TOKEN_CLASS, tokensOf, type Token } from "./yamlTokens";

/** One run of a line's characters drawn alike: the text, its token, and what it declares (`from`, `join.0`, `columns.Name`). */
interface Run {
  text: string;
  token: Token;
  declared: string | null;
}

/** A span that declares a part of the view, with its size, so the most specific one at a place wins. */
interface Linked {
  span: DimensionYamlSpan;
  declared: string;
  size: number;
}

function covers({ span }: Linked, line: number, column: number): boolean {
  if (line < span.line || line > span.endLine) {
    return false;
  }

  if (line === span.line && column < span.column) {
    return false;
  }

  return !(line === span.endLine && column >= span.endColumn);
}

/**
 * A view's item of its flow's YAML, comments and all, each line numbered as in the file. Each part of a line that declares
 * the view's `from`, a join or a column is linked to what it declares: pointing at it lights it here and in the list
 * beside, and pointing at an entry of the list lights the lines that declare it.
 */
export function DimensionViewYaml({ yaml, lit, onPoint }: {
  yaml: DeliveryDimensionYaml | null | undefined;
  /** What is pointed at, here or in the list beside: `from`, `join.<n>` or `columns.<name>`; null for nothing. */
  lit: string | null;
  onPoint: (declared: string | null) => void;
}) {
  const linked = useMemo(() => (yaml == null
    ? []
    : yaml.spans
      .map((span) => ({ span, declared: declaredOf(span.target), size: (span.endLine - span.line) * 10_000 + (span.endColumn - span.column) }))
      .filter((entry): entry is Linked => entry.declared !== null)
      .sort((a, b) => a.size - b.size)), [yaml]);

  const rows = useMemo(() => {
    if (yaml == null) {
      return [];
    }

    const widths = yaml.lines.filter((line) => line.trim() !== "").map((line) => line.length - line.trimStart().length);
    const indent = widths.length === 0 ? 0 : Math.min(...widths);
    return yaml.lines.map((text, index) => {
      const number = yaml.firstLine + index;
      const tokens = tokensOf(text);
      const runs: Run[] = [];
      for (let column = indent + 1; column <= text.length; column++) {
        const token = tokens[column - 1];
        const declared = token === "comment" || token === "space"
          ? null
          : linked.find((entry) => covers(entry, number, column))?.declared ?? null;
        const previous = runs[runs.length - 1];
        if (previous !== undefined && previous.token === token && previous.declared === declared) {
          previous.text += text[column - 1];
        } else {
          runs.push({ text: text[column - 1], token, declared });
        }
      }

      return { number, runs };
    });
  }, [yaml, linked]);

  if (yaml == null) {
    return (
      <section className="flex min-w-0 flex-col rounded-lg border border-border bg-card" data-testid="dimension-view-yaml">
        <YamlHeading file={null} first={null} last={null} copy={null} />
        <p className="px-4 py-6 text-[13px] text-muted-foreground" data-testid="dimension-view-yaml-missing">
          No flow in the catalog declares the view now, so there is no YAML to show.
        </p>
      </section>
    );
  }

  return (
    <section className="flex min-w-0 flex-col overflow-hidden rounded-lg border border-border bg-card" data-testid="dimension-view-yaml">
      <YamlHeading
        file={yaml.file}
        first={yaml.firstLine}
        last={yaml.firstLine + yaml.lines.length - 1}
        copy={yaml.lines.join("\n")}
        copyLabel="Copy the view's YAML"
        copyTestId="dimension-view-yaml-copy"
      />
      <div className="min-w-0 overflow-x-auto py-1.5" onMouseLeave={() => onPoint(null)}>
        <pre className="min-w-max font-mono text-[12px] leading-5">
          {rows.map((row) => {
            const rowLit = lit !== null && row.runs.some((run) => run.declared === lit);
            return (
              <div key={row.number} className={cn("flex border-l-2 pr-4", rowLit ? "border-primary bg-primary/[0.04]" : "border-transparent")} data-testid="dimension-view-yaml-line">
                <span className="w-10 shrink-0 select-none pr-3 text-right tabular-nums text-muted-foreground/60">{row.number}</span>
                <code>
                  {row.runs.map((run, index) => (
                    <span
                      key={index}
                      className={cn(TOKEN_CLASS[run.token], lit !== null && run.declared === lit && "rounded-[3px] bg-primary/15", run.declared !== null && "cursor-default")}
                      onMouseEnter={() => onPoint(run.declared)}
                      data-declared={run.declared ?? undefined}
                    >
                      {run.text}
                    </span>
                  ))}
                </code>
              </div>
            );
          })}
        </pre>
        {yaml.cut && (
          <p className="px-4 pt-1 text-[12px] text-muted-foreground">The view goes on past these lines; the pipeline's page shows the whole file.</p>
        )}
      </div>
    </section>
  );
}
