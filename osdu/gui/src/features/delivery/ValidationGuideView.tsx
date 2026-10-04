import { useState, type ReactNode } from "react";
import { ChevronRight, ExternalLink } from "lucide-react";
import { CopyButton } from "@/components/CopyButton";
import { cn } from "@/lib/utils";
import type { FindingGuide, ValidationGuidance, ValueExpectation } from "../../api/validation";

/** A value the schema or OSDU's example writes, as it is written, with a copy button. */
function Written({ text, testId }: { text: string; testId: string }) {
  return (
    <span className="inline-flex min-w-0 max-w-full items-start gap-1">
      <code className="min-w-0 break-all rounded bg-muted px-1 py-px font-mono text-[11px]" data-testid={testId}>{text}</code>
      <CopyButton label="Copy" text={text} testId={`${testId}-copy`} iconOnly />
    </span>
  );
}

function Pair({ term, children }: { term: string; children: ReactNode }) {
  return (
    <>
      <dt className="text-muted-foreground">{term}</dt>
      <dd className="min-w-0">{children}</dd>
    </>
  );
}

/**
 * What the schema says of the value where a finding is (osdu/docs/validation-plan.md, Guidance): its title and description,
 * what it takes, whether it is required, its patterns, the values it allows, the records it points to, what each item or
 * property is, its own examples, and the value OSDU's example record holds there.
 */
export function ExpectationDetails({ expected, testId }: { expected: ValueExpectation; testId: string }) {
  const named = expected.properties.map((name) => (expected.requiredProperties.includes(name) ? `${name} *` : name));
  return (
    <dl className="grid grid-cols-[max-content_minmax(0,1fr)] gap-x-3 gap-y-1 rounded-md bg-muted/40 px-2.5 py-2 text-[12px]" data-testid={testId}>
      {expected.title !== null && <Pair term="Title">{expected.title}</Pair>}
      {expected.description !== null && <Pair term="Description"><span className="whitespace-pre-wrap" data-testid={`${testId}-description`}>{expected.description}</span></Pair>}
      <Pair term="Takes">{expected.summary}</Pair>
      <Pair term="Required">{expected.required ? "yes" : "no: it may be left out"}</Pair>
      {expected.patterns.length > 0 && (
        <Pair term={expected.patterns.length === 1 ? "Pattern" : "Patterns"}>
          <span className="flex flex-col gap-0.5">
            {expected.patterns.map((pattern, index) => <Written key={pattern} text={pattern} testId={`${testId}-pattern-${index}`} />)}
          </span>
        </Pair>
      )}
      {expected.allowedCount > 0 && (
        <Pair term={expected.allowedCount === 1 ? "Allowed" : `Allowed (${expected.allowedCount.toLocaleString("en-US")})`}>
          <span className="flex flex-wrap gap-1" data-testid={`${testId}-allowed`}>
            {expected.allowed.map((value) => <code key={value} className="rounded bg-muted px-1 py-px font-mono text-[11px]">{value}</code>)}
            {expected.allowedCount > expected.allowed.length && (
              <span className="text-muted-foreground">{`and ${(expected.allowedCount - expected.allowed.length).toLocaleString("en-US")} more`}</span>
            )}
          </span>
        </Pair>
      )}
      {expected.entityTypes.length > 0 && <Pair term="Points to">{expected.entityTypes.join(", ")}</Pair>}
      {expected.items !== null && <Pair term="Each item">{expected.items}</Pair>}
      {named.length > 0 && (
        <Pair term="Properties">
          <span className="text-muted-foreground">
            {named.join(", ")}
            {expected.propertyCount > named.length ? `, and ${(expected.propertyCount - named.length).toLocaleString("en-US")} more` : ""}
            {expected.requiredProperties.length > 0 ? " (* required)" : ""}
            {expected.onlyNamedProperties ? "; no other property is allowed" : ""}
          </span>
        </Pair>
      )}
      {expected.examples.length > 0 && (
        <Pair term={expected.examples.length === 1 ? "Example" : "Examples"}>
          <span className="flex flex-col gap-0.5">
            {expected.examples.map((example, index) => <Written key={example} text={example} testId={`${testId}-example-${index}`} />)}
          </span>
        </Pair>
      )}
      {expected.osduExample !== null && (
        <Pair term="OSDU's example">
          <Written text={expected.osduExample} testId={`${testId}-osdu-example`} />
        </Pair>
      )}
    </dl>
  );
}

/**
 * A finding's guidance under it: the value found and what the schema takes there, how to fix it, and what the schema says
 * of the place, folded until asked for.
 */
export function FindingGuideLines({ guide, expected, testId }: { guide: FindingGuide; expected: ValueExpectation | null; testId: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="flex min-w-0 flex-col gap-1" data-testid={`${testId}-guide`}>
      <div className="flex flex-wrap gap-x-4 gap-y-0.5 text-muted-foreground">
        {guide.found !== "" && (
          <span className="min-w-0">
            {"Found "}
            <span className="break-all font-mono text-[11px] text-foreground" data-testid={`${testId}-found`}>{guide.found}</span>
          </span>
        )}
        {expected !== null && (
          <span className="min-w-0">
            {"Takes "}
            <span className="text-foreground" data-testid={`${testId}-expected`}>{expected.summary}</span>
            {expected.required ? " (required)" : ""}
          </span>
        )}
      </div>
      {guide.advice !== null && (
        <p className="text-foreground" data-testid={`${testId}-advice`}>
          <span className="font-medium">{"Fix: "}</span>
          {guide.advice}
        </p>
      )}
      {expected !== null && (
        <>
          <button
            type="button"
            className="inline-flex w-fit cursor-pointer items-center gap-1 text-[11.5px] text-primary hover:underline"
            aria-expanded={open}
            onClick={() => setOpen((was) => !was)}
            data-testid={`${testId}-details-toggle`}
          >
            <ChevronRight className={cn("size-3 transition-transform", open && "rotate-90")} aria-hidden />
            What the schema says here
          </button>
          {open && <ExpectationDetails expected={expected} testId={`${testId}-details`} />}
        </>
      )}
    </div>
  );
}

/** Where the values quoted as OSDU's example come from, or why none is quoted. */
export function GuidanceSource({ guidance }: { guidance: ValidationGuidance }) {
  if (guidance.example !== null) {
    return (
      <p className="text-[11.5px] text-muted-foreground" data-testid="validation-example-source">
        {"OSDU's examples are from the OSDU data definitions, release "}
        {guidance.example.release}
        {": "}
        <a href={guidance.example.webUrl} target="_blank" rel="noreferrer" className="inline-flex items-center gap-0.5 font-mono text-[11px] text-primary hover:underline">
          {guidance.example.path}
          <ExternalLink className="size-3" aria-hidden />
        </a>
      </p>
    );
  }

  return guidance.exampleNote === null ? null : <p className="text-[11.5px] text-muted-foreground" data-testid="validation-example-source">{guidance.exampleNote}</p>;
}
