import { useMemo } from "react";
import { FileCode } from "lucide-react";
import { CopyButton } from "@/components/CopyButton";
import { cn } from "@/lib/utils";
import type { DeliveryDimensionYaml, DimensionYamlSpan } from "../../../api/delivery";
import { elementsOfTarget, isContainer, keyNameOf, type BlueprintElement, type BlueprintLayout } from "./blueprintModel";
import type { BlueprintLight } from "./BlueprintDiagram";

interface YamlProps {
  yaml: DeliveryDimensionYaml | null;
  /** Why no YAML is shown, when none is. */
  missing: string | null;
  layout: BlueprintLayout;
  light: BlueprintLight | null;
  onPoint: (elements: BlueprintElement[] | null) => void;
  onPick: (element: BlueprintElement) => void;
}

/** How a character of a line is drawn: a key, a value, punctuation or a comment. */
type Token = "key" | "value" | "mark" | "comment" | "space";

/** One run of a line's characters drawn alike: the text, its token, whether it is lit, and what it declares. */
interface Run {
  text: string;
  token: Token;
  lit: boolean;
  target: string | null;
}

/** A span's place in the block: the targets that declare parts of the diagram, the most specific first. */
interface Declared {
  span: DimensionYamlSpan;
  elements: BlueprintElement[];
  /** Only the key of a span that holds others lights, so `attributes:` does not light every attribute's lines. */
  keyOnly: boolean;
  size: number;
}

/** The kind of each character of a YAML line, as a reader tells them apart. */
function tokensOf(line: string): Token[] {
  const tokens: Token[] = new Array(line.length).fill("value");
  let quote: string | null = null;
  let comment = -1;
  for (let at = 0; at < line.length; at++) {
    const c = line[at];
    if (quote !== null) {
      if (c === quote) {
        quote = null;
      }

      continue;
    }

    if (c === "'" || c === "\"") {
      quote = c;
      continue;
    }

    if (c === "#" && (at === 0 || /\s/.test(line[at - 1]))) {
      comment = at;
      break;
    }
  }

  const end = comment < 0 ? line.length : comment;
  for (let at = end; at < line.length; at++) {
    tokens[at] = "comment";
  }

  // A key: what stands before the first colon followed by a space or the end of the line, after an optional dash.
  const key = /^(\s*)(- )?([A-Za-z_][\w-]*)(:)(?=\s|$)/.exec(line.slice(0, end));
  let from = 0;
  if (key !== null) {
    const [, indent, dash = "", name] = key;
    for (let at = indent.length; at < indent.length + dash.length; at++) {
      tokens[at] = "mark";
    }

    const start = indent.length + dash.length;
    for (let at = start; at < start + name.length; at++) {
      tokens[at] = "key";
    }

    tokens[start + name.length] = "mark";
    from = start + name.length + 1;
  } else {
    const dash = /^(\s*)(- )/.exec(line.slice(0, end));
    if (dash !== null) {
      from = dash[1].length + 2;
      tokens[dash[1].length] = "mark";
    }
  }

  // The indentation belongs to nothing written on the line.
  for (let at = 0; at < end && line[at] === " "; at++) {
    tokens[at] = "space";
  }

  // Inside a flow collection the brackets, commas and the colons of its keys are punctuation.
  quote = null;
  for (let at = from; at < end; at++) {
    const c = line[at];
    if (quote !== null) {
      if (c === quote) {
        quote = null;
      }

      continue;
    }

    if (c === "'" || c === "\"") {
      quote = c;
    } else if ("[]{},".includes(c) || (c === ":" && (line[at + 1] === " " || at + 1 === end))) {
      tokens[at] = "mark";
    }
  }

  return tokens;
}

const TOKEN_CLASS: Record<Token, string> = {
  key: "text-primary",
  value: "text-foreground",
  mark: "text-muted-foreground",
  comment: "italic text-muted-foreground/80",
  space: "",
};

/**
 * The dimension's YAML as its flow writes it, comments and all, each line numbered as in the file. Each part of a line
 * that declares something is linked to the diagram: pointing at `label: data.FacilityName` lights the read and the column
 * it makes, and pointing at a part of the diagram lights the YAML that declares it. A click picks it, which the panel
 * beside explains.
 */
export function BlueprintYaml({ yaml, missing, layout, light, onPoint, onPick }: YamlProps) {
  const declared = useMemo(() => {
    if (yaml === null) {
      return [];
    }

    return yaml.spans
      .map((span): Declared => ({
        span,
        elements: elementsOfTarget(layout, span.target),
        keyOnly: isContainer(span.target, yaml.spans),
        size: (span.endLine - span.line) * 10_000 + (span.endColumn - span.column),
      }))
      .filter((d) => d.elements.length > 0)
      .sort((a, b) => a.size - b.size);
  }, [yaml, layout]);

  const indent = useMemo(() => {
    if (yaml === null) {
      return 0;
    }

    const widths = yaml.lines.filter((line) => line.trim() !== "").map((line) => line.length - line.trimStart().length);
    return widths.length === 0 ? 0 : Math.min(...widths);
  }, [yaml]);

  if (yaml === null) {
    return (
      <section className="flex min-w-0 flex-col rounded-lg border border-border bg-card" data-testid="blueprint-yaml">
        <YamlHeading file={null} first={null} last={null} copy={null} />
        <p className="px-4 py-6 text-[13px] text-muted-foreground" data-testid="blueprint-yaml-missing">{missing ?? "The flow's YAML is not available."}</p>
      </section>
    );
  }

  // A declaration that holds others (attributes:) lights only when all it declares is lit, else every attribute would light it.
  const lit = (d: Declared) => light !== null && (d.keyOnly
    ? d.elements.every((element) => light.parts.has(element))
    : d.elements.some((element) => light.parts.has(element)));

  /** The most specific declaration at a place, for pointing; only the key of one that holds others. */
  const at = (line: number, column: number): Declared | null => declared.find((d) => covers(d, line, column)) ?? null;

  const rows = yaml.lines.map((text, index) => {
    const number = yaml.firstLine + index;
    const tokens = tokensOf(text);
    const runs: Run[] = [];
    for (let column = 1; column <= text.length; column++) {
      if (column <= indent) {
        continue;
      }

      const token = tokens[column - 1];
      const here = token === "comment" || token === "space" ? null : at(number, column);
      const shine = here !== null && lit(here);
      const target = here?.span.target ?? null;
      const previous = runs[runs.length - 1];
      if (previous !== undefined && previous.token === token && previous.lit === shine && previous.target === target) {
        previous.text += text[column - 1];
      } else {
        runs.push({ text: text[column - 1], token, lit: shine, target });
      }
    }

    return { number, runs, lit: runs.some((run) => run.lit) };
  });

  const first = yaml.firstLine;
  const last = yaml.firstLine + yaml.lines.length - 1;
  const elementsOf = (target: string | null) => (target === null ? [] : declared.find((d) => d.span.target === target)?.elements ?? []);
  return (
    <section className="flex min-w-0 flex-col overflow-hidden rounded-lg border border-border bg-card" data-testid="blueprint-yaml">
      <YamlHeading file={yaml.file} first={first} last={last} copy={yaml.lines.join("\n")} />
      <div className="min-w-0 overflow-x-auto py-1.5" onMouseLeave={() => onPoint(null)}>
        <pre className="min-w-max font-mono text-[12px] leading-5">
          {rows.map((row) => (
            <div key={row.number} className={cn("flex border-l-2 pr-4", row.lit ? "border-primary bg-primary/[0.04]" : "border-transparent")} data-testid="blueprint-yaml-line">
              <span className="w-10 shrink-0 select-none pr-3 text-right tabular-nums text-muted-foreground/60">{row.number}</span>
              <code>
                {row.runs.map((run, index) => {
                  const elements = elementsOf(run.target);
                  const linked = elements.length > 0;
                  return (
                    <span
                      key={index}
                      className={cn(
                        TOKEN_CLASS[run.token],
                        run.lit && "rounded-[3px] bg-primary/15",
                        linked && "cursor-pointer",
                      )}
                      onMouseEnter={linked ? () => onPoint(elements) : () => onPoint(null)}
                      onClick={linked ? () => onPick(elements[0]) : undefined}
                      data-target={run.target ?? undefined}
                    >
                      {run.text}
                    </span>
                  );
                })}
              </code>
            </div>
          ))}
        </pre>
        {yaml.cut && (
          <p className="px-4 pt-1 text-[12px] text-muted-foreground">The dimension goes on past these lines; the pipeline's page shows the whole file.</p>
        )}
      </div>
    </section>
  );
}

/** Whether a declaration covers a place: its whole span, or only its key's name for one that holds others. */
function covers(d: Declared, line: number, column: number): boolean {
  const { span } = d;
  if (d.keyOnly) {
    return line === span.line && column >= span.column && column < span.column + keyNameOf(span.target).length;
  }

  if (line < span.line || line > span.endLine) {
    return false;
  }

  if (line === span.line && column < span.column) {
    return false;
  }

  return !(line === span.endLine && column >= span.endColumn);
}

function YamlHeading({ file, first, last, copy }: { file: string | null; first: number | null; last: number | null; copy: string | null }) {
  return (
    <header className="flex min-w-0 items-center gap-2 border-b border-border bg-muted/40 px-3 py-2">
      <FileCode className="size-3.5 shrink-0 text-muted-foreground" aria-hidden />
      <span className="text-[13px] font-semibold">YAML</span>
      {file !== null && <span className="min-w-0 truncate font-mono text-[11.5px] text-muted-foreground">{file}</span>}
      {first !== null && last !== null && <span className="shrink-0 text-[11.5px] text-muted-foreground">lines {first}–{last}</span>}
      {copy !== null && (
        <span className="ml-auto shrink-0">
          <CopyButton iconOnly label="Copy the dimension's YAML" text={copy} testId="blueprint-yaml-copy" />
        </span>
      )}
    </header>
  );
}
