import type { ReactNode } from "react";
import { ChevronRight } from "lucide-react";
import { CopyButton } from "@/components/CopyButton";
import { DataTable } from "@/components/DataTable";
import { RichTooltip } from "@/components/RichTooltip";
import { cn } from "@/lib/utils";
import { KindText } from "../KindText";
import { fieldText, type DimensionEntry } from "./dimensionFormat";

/** The columns every dimension's table has, in the order the table holds them, with what each is. */
const FIXED_COLUMNS: { name: string; hint: string }[] = [
  { name: "id", hint: "The row's number, the table's key: what a table of facts joins on. It stays the same for as long as the dimension holds the row." },
  { name: "partition", hint: "The data partition the row was read in. A flow that builds in several partitions writes them all to this table." },
  { name: "key_id", hint: "The key's number: the same in every row of the key, for a dimension that collects several values a key." },
  { name: "key", hint: "The key exactly as the index holds it (an id, for a reference)." },
  { name: "value", hint: "The key's human-friendly value: its label, cleaned." },
  { name: "records", hint: "The records of the row: those holding the value the row collects, or every record of the key." },
  { name: "filter", hint: "The search that finds the key's records." },
];

/** One part of the definition: what it is about, in a line, and its facts in rows that line up. */
function Section({ title, hint, children, className, testId }: {
  title: string;
  hint: string;
  children: ReactNode;
  className?: string;
  testId: string;
}) {
  return (
    <section className={cn("min-w-0 overflow-hidden rounded-lg border border-border bg-card", className)} data-testid={testId}>
      <header className="flex flex-wrap items-baseline gap-x-2 gap-y-0.5 border-b border-border bg-muted/30 px-4 py-2">
        <h3 className="text-[13px] font-semibold">{title}</h3>
        <span className="text-[12px] text-muted-foreground">{hint}</span>
      </header>
      <dl className="divide-y divide-border/60">{children}</dl>
    </section>
  );
}

/** One fact of a section: what it is on the left, always the same width, and the fact itself on the right. */
function Row({ label, children, testId }: { label: ReactNode; children: ReactNode; testId?: string }) {
  return (
    <div className="grid grid-cols-[9.5rem_minmax(0,1fr)] items-baseline gap-x-4 px-4 py-2" data-testid={testId}>
      <dt className="text-[12px] text-muted-foreground">{label}</dt>
      <dd className="min-w-0 text-[13px]">{children}</dd>
    </div>
  );
}

/** Text a person copies or reads as code: a path, a query, a name. */
function Code({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn("break-all font-mono text-[12px]", className)}>{children}</span>;
}

/**
 * What a text is read through, left to right: where it starts, each step, and what it ends as. The steps wrap inside
 * their own cell, so a long path never pushes its row out of line.
 */
function Steps({ from, steps, to, testId }: { from: string; steps: string[]; to: string; testId: string }) {
  return (
    <ol className="flex min-w-0 flex-wrap items-center gap-x-1 gap-y-1" data-testid={testId}>
      <li className="text-[12px] text-muted-foreground">{from}</li>
      {steps.map((step, index) => (
        <li key={`${index}:${step}`} className="flex min-w-0 items-center gap-1">
          <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/70" aria-hidden />
          <span className="min-w-0 break-all rounded-sm border border-border bg-secondary/60 px-1.5 py-0.5 font-mono text-[12px] dark:bg-input/40" data-testid={`${testId}-step`}>
            {step}
          </span>
        </li>
      ))}
      <li className="flex items-center gap-1 whitespace-nowrap">
        <ChevronRight className="size-3.5 shrink-0 text-muted-foreground/70" aria-hidden />
        <span className="text-[12px] font-medium">{to}</span>
      </li>
    </ol>
  );
}

/** A column of the dimension's table, with what it holds on hover; an attribute's column is told from the fixed ones. */
function ColumnChip({ name, hint, attribute = false }: { name: string; hint: string; attribute?: boolean }) {
  return (
    <RichTooltip body={hint}>
      <span
        className={cn(
          "rounded-sm border px-1.5 py-0.5 font-mono text-[12px]",
          attribute ? "border-primary/40 bg-primary/8 text-foreground" : "border-border bg-secondary/60 dark:bg-input/40",
        )}
        data-testid="dimension-table-column"
      >
        {name}
      </span>
    </RichTooltip>
  );
}

/**
 * How the flow declares a dimension, and what its builds make of it, in four parts whose facts line up: the table it is
 * written to (its name, its columns and the query that reads it), what a build reads from the search, how a key becomes
 * its value, and the attributes read of each key. Under them, the kinds its last build read with the template of each.
 */
export function DimensionDefinition({ entry }: { entry: DimensionEntry }) {
  const { dimension, flow } = entry;
  const kinds = dimension.current?.kinds ?? [];
  const labelled = dimension.label.length > 0;
  const query = dimension.table === null
    ? null
    : `SELECT * FROM ${dimension.table}${flow.partition === null ? "" : ` WHERE [partition] = N'${flow.partition.replaceAll("'", "''")}'`};`;
  return (
    <div className="flex flex-col gap-3" data-testid="dimension-definition">
      <Section
        title="Table"
        hint="The dimension as one table in the database: a row per key and value it collects. A run writes it."
        testId="dimension-definition-table"
      >
        <Row label="Name" testId="dimension-definition-table-name">
          {dimension.table === null
            ? <span className="text-muted-foreground">Made by the next run of the pipeline.</span>
            : (
              <span className="flex min-w-0 items-center gap-1.5">
                <Code className="font-medium">{dimension.table}</Code>
                <CopyButton iconOnly label="Copy the table's name" text={dimension.table} testId="dimension-copy-table" />
              </span>
            )}
        </Row>
        <Row label="Columns">
          <span className="flex flex-wrap gap-1">
            {FIXED_COLUMNS.map((column) => <ColumnChip key={column.name} name={column.name} hint={column.hint} />)}
            {dimension.attributes.map((attribute) => (
              <ColumnChip
                key={attribute.name}
                name={attribute.name}
                attribute
                hint={attribute.collect === null
                  ? `The attribute ${attribute.name}, read from the record the key names.`
                  : `The attribute ${attribute.name}, collected from the key's records: a row for each value a key holds.`}
              />
            ))}
          </span>
        </Row>
        <Row label="Joined on">
          <span><Code>id</Code> for a row, <Code>key_id</Code> for a key. An attribute the flow starts to declare gets its column on the next run.</span>
        </Row>
        {query !== null && (
          <Row label="Read it">
            <span className="flex min-w-0 items-center gap-1.5">
              <Code>{query}</Code>
              <CopyButton iconOnly label="Copy the query" text={query} testId="dimension-copy-table-query" />
            </span>
          </Row>
        )}
      </Section>

      <div className="grid min-w-0 gap-3 xl:grid-cols-2">
        <Section title="Reads" hint="What a build reads from the OSDU search." testId="dimension-definition-reads">
          <Row label="Kind"><KindText kind={dimension.kind} /></Row>
          <Row label="Path"><Code>{dimension.path}</Code></Row>
          <Row label="Query">
            {dimension.query === null ? <span className="text-muted-foreground">Every record of the kind.</span> : <Code>{dimension.query}</Code>}
          </Row>
          {dimension.builtQuery !== null && dimension.builtQuery !== dimension.query && (
            <Row label="Query as built"><Code>{dimension.builtQuery}</Code></Row>
          )}
          <Row label="Field in the index">
            {dimension.field === null
              ? <span className="text-muted-foreground">Settled by its first build.</span>
              : (
                <span className="flex flex-col gap-0.5">
                  <span>{fieldText(dimension.field)}</span>
                  <span className="text-[12px] text-muted-foreground">aggregated as <Code className="text-foreground">{dimension.field.aggregateBy}</Code></span>
                </span>
              )}
          </Row>
          <Row label="Records per value">
            {dimension.countRecords ? "Counted exactly, a search per value." : "Exact where a record holds one key, else summed."}
          </Row>
          <Row label="Most keys read"><span className="font-mono tabular-nums">{dimension.maxValues.toLocaleString("en-US")}</span></Row>
        </Section>

        <Section title="Values" hint="How a key becomes the value a person picks." testId="dimension-definition-values">
          <Row label="Label">
            {labelled
              ? <Steps from="the record the key names" steps={dimension.label} to="label" testId="dimension-label-steps" />
              : <span className="text-muted-foreground">None: each key is cleaned into its value itself.</span>}
          </Row>
          {labelled && (
            <Row label="Without a label" testId="dimension-unlabelled">
              {dimension.unlabelled === null
                ? "Valued by the code its id ends with."
                : <span>Valued <span className="font-medium">{dimension.unlabelled}</span></span>}
            </Row>
          )}
          <Row label="Clean">
            {dimension.clean.length === 0
              ? <span className="text-muted-foreground">{labelled ? "None: each key's label is its value, trimmed." : "None: each key is its own value, trimmed."}</span>
              : <Steps from={labelled ? "label" : "key"} steps={dimension.clean} to="value" testId="dimension-clean-steps" />}
          </Row>
        </Section>
      </div>

      {dimension.attributes.length > 0 && (
        <Section title="Attributes" hint="What else is read of each key: a column of the table each." testId="dimension-attributes">
          {dimension.attributes.map((attribute) => (
            <Row key={attribute.name} label={<span className="font-mono text-[12px] font-medium text-foreground">{attribute.name}</span>}>
              {attribute.collect === null
                ? <Steps from="the record the key names" steps={attribute.steps} to={attribute.name} testId="dimension-attribute-steps" />
                : (
                  <span className="flex flex-wrap items-center gap-x-1.5 gap-y-1" data-testid="dimension-attribute-collect">
                    <span className="text-[12px] text-muted-foreground">collected from each key&apos;s own records at</span>
                    <span className="rounded-sm border border-border bg-secondary/60 px-1.5 py-0.5 font-mono text-[12px] dark:bg-input/40">{attribute.collect}</span>
                    <span className="text-[12px] text-muted-foreground">: a row for each value a key holds</span>
                  </span>
                )}
            </Row>
          ))}
        </Section>
      )}

      {kinds.length > 0 && (
        <section className="flex flex-col gap-2">
          <h3 className="text-[13px] font-semibold">Kinds its last build read</h3>
          <DataTable
            columns={[
              { id: "kind", header: "Kind", fill: true, floor: 240, render: (row) => <KindText kind={row.kind} /> },
              { id: "records", header: "Records", align: "right", render: (row) => <span className="font-mono text-[12px] tabular-nums">{row.records.toLocaleString("en-US")}</span> },
              { id: "template", header: "Template read", render: (row) => <span className="font-mono text-[12px] text-muted-foreground">{row.template ?? "none saved"}</span> },
            ]}
            rows={kinds}
            rowKey={(row) => row.kind}
            emptyMessage="No kind was read."
            data-testid="dimension-definition-kinds"
          />
        </section>
      )}
    </div>
  );
}
