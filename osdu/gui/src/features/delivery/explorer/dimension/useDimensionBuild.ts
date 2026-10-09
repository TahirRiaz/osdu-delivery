import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { keepPreviousData, useQuery } from "@tanstack/react-query";
import { toast } from "sonner";
import {
  composeDimension, dimensionKeyCandidates, dimensionKeys, explorerApi, type DimensionDraft, type DimensionKeysRequest,
} from "../../../../api/explorer";
import {
  DRAFT_PARAM, EXAMPLES, applyPlan, chooseKey, draftFromParam, draftParam, emptyDraft, firstValueAt, kindMatches, planOf,
  type AmbiguousPick, type BuildPick, type PickPlan, type SegmentFilter,
} from "./dimensionDraft";

/** How long a draft rests before it is written and checked again: a person typing a name is not asked about every letter. */
const SETTLE_MS = 350;

/** How long the keys read stand before the same are read again. */
const FRESH_MS = 60_000;

/** `value`, once it has stopped changing for `ms`. */
function useSettled<T>(value: T, ms: number): T {
  const [settled, setSettled] = useState(value);
  useEffect(() => {
    const timer = window.setTimeout(() => setSettled(value), ms);
    return () => window.clearTimeout(timer);
  }, [value, ms]);
  return settled;
}

const isText = (value: unknown): value is string => typeof value === "string";

/**
 * The dimension a person builds in the explorer (osdu/docs/reference/concepts/explorer.md, Building a dimension): the
 * draft, held here and changed at once as any input is, with the explorer's address following it (its `dim` part,
 * written over the address as it stands, so the explorer's own navigation and the build never undo each other); the
 * keys the kind's template suggests, the likeliest made the key as a build starts; the commonest keys and the record in
 * view, which the example steps through, that record's own key first; the YAML and its check, once the draft has
 * settled; and the picks made in the records on the explorer's trail, each asked about where it passes through one item
 * of a list.
 */
export function useDimensionBuild({ partition, reachable, scopeKind, recordId, recordVersion }: {
  partition: string | null;
  reachable: boolean;
  /** The kind the explorer's place reads (a type's every version), which a build that has no kind yet takes; null for none. */
  scopeKind: string | null;
  /** The record the explorer has open, the first of its trail; null for none. */
  recordId: string | null;
  recordVersion: number | null;
}) {
  const [params, setParams] = useSearchParams();
  const [draft, setDraft] = useState<DimensionDraft | null>(() => {
    const held = params.get(DRAFT_PARAM);
    return held === null ? null : draftFromParam(held);
  });

  // The address follows the build: written over the address as it stands now, which the router has already moved to when a
  // navigation of the explorer's is still being applied, and only where it says something else.
  const address = params.toString();
  useEffect(() => {
    const current = new URLSearchParams(window.location.search);
    const wanted = draft === null ? null : draftParam(draft);
    if (current.get(DRAFT_PARAM) === wanted) {
      return;
    }

    if (wanted === null) {
      current.delete(DRAFT_PARAM);
    } else {
      current.set(DRAFT_PARAM, wanted);
    }

    setParams(current, { replace: true });
  }, [draft, address, setParams]);

  // A build with no kind yet reads the place the explorer is at, once it is at one.
  if (draft !== null && draft.kind === "" && scopeKind !== null) {
    setDraft({ ...draft, kind: scopeKind });
  }

  const kind = draft?.kind ?? "";
  const building = draft !== null;

  // The record in view, read as the explorer reads it (the same read, shared), when it is one of the kind the build reads.
  const rootRead = useQuery({
    queryKey: ["explorer", "read", partition, recordId, recordVersion],
    queryFn: () => explorerApi.read(partition, recordId!, recordVersion ?? undefined),
    enabled: building && reachable && recordId !== null,
    staleTime: Number.POSITIVE_INFINITY,
    gcTime: 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const read = recordId === null ? null : rootRead.data ?? null;
  const record = read?.found === true && read.record ? read.record : null;
  const recordKind = record !== null && isText(record.kind) ? record.kind : null;
  const root = record !== null && recordKind !== null && (kind === "" || kindMatches(kind, recordKind)) ? record : null;

  const candidates = useQuery({
    queryKey: ["explorer", "dimension", "candidates", kind],
    queryFn: () => dimensionKeyCandidates(kind),
    enabled: building && reachable && kind !== "",
    staleTime: 5 * 60_000,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const suggestions = candidates.data?.keys ?? null;
  const namesOf = (path: string) => suggestions?.find((candidate) => candidate.path === path)?.names ?? [];

  // As a build starts, the likeliest key the template suggests is made the key: the first the record in view holds a value
  // at, once that record has been read. A key picked since, or a build that had one, is left as it is.
  const [autoFor, setAutoFor] = useState<string | null>(null);
  const recordSettled = recordId === null || !rootRead.isPending;
  if (draft !== null && kind !== "" && (draft.path === null || draft.path === "") && suggestions !== null && recordSettled && autoFor !== kind) {
    setAutoFor(kind);
    const chosen = chooseKey(suggestions, root);
    if (chosen !== null) {
      const plan: PickPlan = { action: "key", kind, key: chosen.path, keyExample: firstValueAt(root, chosen.path), path: chosen.path, steps: [], questions: [] };
      setDraft(applyPlan(draft, plan, [], [], chosen.names));
    }
  }

  const settled = useSettled(draft, SETTLE_MS);
  const keysRequest: DimensionKeysRequest | null = settled !== null && settled.kind !== "" && settled.path !== null && settled.path !== ""
    ? { kind: settled.kind, query: settled.query, path: settled.path }
    : null;
  const keys = useQuery({
    queryKey: ["explorer", "dimension", "keys", partition, keysRequest],
    queryFn: () => dimensionKeys(partition, keysRequest!),
    enabled: reachable && keysRequest !== null,
    staleTime: FRESH_MS,
    retry: false,
    refetchOnWindowFocus: false,
  });

  // The examples: the key the record in view holds first, then the commonest keys.
  const recordKey = draft?.path ? firstValueAt(root, draft.path) : null;
  const examples = [...new Set([recordKey, ...(keys.data?.answer.keys ?? []).map((k) => k.key)].filter(isText))].slice(0, EXAMPLES);
  const [at, setAt] = useState(0);
  const exampleFor = `${draft?.path ?? ""}|${recordKey ?? ""}`;
  const [seenExampleFor, setSeenExampleFor] = useState(exampleFor);
  if (seenExampleFor !== exampleFor) {
    setSeenExampleFor(exampleFor);
    setAt(0);
  }

  const exampleKey = examples.length === 0 ? null : examples[Math.min(at, examples.length - 1)];

  const compose = useQuery({
    queryKey: ["explorer", "dimension", "compose", partition, settled, exampleKey],
    queryFn: () => composeDimension(partition, settled!, exampleKey),
    enabled: reachable && settled !== null && settled.kind !== "",
    placeholderData: keepPreviousData,
    retry: false,
    refetchOnWindowFocus: false,
  });
  const columns = [compose.data?.dimension?.keyColumn, compose.data?.dimension?.valueColumn].filter(isText);

  // A pick waiting on its questions, answered one at a time; the draft is changed once every one is.
  const [pending, setPending] = useState<{ plan: PickPlan; answers: (SegmentFilter | null)[] } | null>(null);
  const finish = (plan: PickPlan, answers: (SegmentFilter | null)[]) =>
    setDraft((current) => (current === null ? current : applyPlan(current, plan, answers, columns, namesOf(plan.key))));

  return {
    /** The draft; null while no dimension is built. */
    draft,
    /** Starts a build reading `start` (empty for none yet), leaving any build before it. */
    start: (start: string) => {
      setAutoFor(null);
      setPending(null);
      setAt(0);
      setDraft(emptyDraft(start));
    },
    /** Ends the build, its draft with it. */
    stop: () => {
      setPending(null);
      setDraft(null);
    },
    /** Replaces the draft with what the table's settings make of it. */
    update: (next: DimensionDraft) => setDraft(next),
    /** Makes a pick in a record on the explorer's trail: at once, or once the questions it raises are answered. */
    pick: (pick: BuildPick) => {
      if (draft === null) {
        return;
      }

      const plan = planOf(draft, pick);
      if ("problem" in plan) {
        toast.error(plan.problem);
      } else if (plan.questions.length === 0) {
        finish(plan, []);
      } else {
        setPending({ plan, answers: [] });
      }
    },
    /** The question the pick waiting asks now; null when none waits. */
    question: (pending?.plan.questions[pending.answers.length] ?? null) as AmbiguousPick | null,
    /** Answers the question asked: a filter, or none to keep the first value found (or follow every record). */
    answer: (filter: SegmentFilter | null) => {
      if (pending === null) {
        return;
      }

      const answers = [...pending.answers, filter];
      if (answers.length === pending.plan.questions.length) {
        setPending(null);
        finish(pending.plan, answers);
      } else {
        setPending({ ...pending, answers });
      }
    },
    /** Leaves the pick waiting unmade. */
    cancel: () => setPending(null),
    candidates,
    keys,
    compose,
    /** The kind of the record in view, whichever kind the build reads. */
    recordKind,
    /** The examples the build steps through, the record in view's key first, and the one shown. */
    examples,
    exampleAt: Math.min(at, Math.max(0, examples.length - 1)),
    exampleKey,
    recordKey,
    stepExample: (step: number) => setAt((current) => (examples.length === 0 ? 0 : (current + step + examples.length) % examples.length)),
  };
}

/** What a build holds and does, as the explorer's page and the build panel use it. */
export type DimensionBuild = ReturnType<typeof useDimensionBuild>;
