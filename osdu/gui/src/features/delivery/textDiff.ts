/** A run of text both sides share, or one that only the older or only the newer side holds. */
export interface DiffPart {
  kind: "same" | "removed" | "added";
  text: string;
}

/**
 * Beyond this many token pairs (after the common start and end are set aside) a word diff costs more than it tells, and
 * the two texts are compared whole instead.
 */
const MAX_CELLS = 250_000;

/** Words, runs of whitespace, and single punctuation marks, so a changed full stop or regex class reads as its own edit. */
function tokens(text: string): string[] {
  return text.match(/\s+|[\p{L}\p{N}_]+|[^\s\p{L}\p{N}_]/gu) ?? [];
}

function append(parts: DiffPart[], kind: DiffPart["kind"], text: string): void {
  const last = parts.at(-1);
  if (last !== undefined && last.kind === kind) {
    last.text += text;
  } else {
    parts.push({ kind, text });
  }
}

/**
 * Consecutive edits read as one removal followed by one addition, and a lone space between two edits belongs to both,
 * so "the Depth of" to "the measured depth of" shows one struck phrase and one new phrase rather than a word salad.
 */
function tidy(parts: DiffPart[]): DiffPart[] {
  const out: DiffPart[] = [];
  let removed = "";
  let added = "";
  const flush = () => {
    if (removed !== "") {
      out.push({ kind: "removed", text: removed });
    }

    if (added !== "") {
      out.push({ kind: "added", text: added });
    }

    removed = "";
    added = "";
  };

  parts.forEach((part, index) => {
    if (part.kind === "removed") {
      removed += part.text;
    } else if (part.kind === "added") {
      added += part.text;
    } else if ((removed !== "" || added !== "") && index < parts.length - 1 && /^\s+$/.test(part.text)) {
      removed += part.text;
      added += part.text;
    } else {
      flush();
      out.push({ ...part });
    }
  });
  flush();
  return out;
}

/**
 * What changed between two texts, word by word (a longest common subsequence over {@link tokens}). Null when the texts
 * are too long to compare that way; the caller then shows the two whole.
 */
export function diffText(before: string, after: string): DiffPart[] | null {
  const a = tokens(before);
  const b = tokens(after);
  let start = 0;
  while (start < a.length && start < b.length && a[start] === b[start]) {
    start++;
  }

  let endA = a.length;
  let endB = b.length;
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) {
    endA--;
    endB--;
  }

  const n = endA - start;
  const m = endB - start;
  if (n * m > MAX_CELLS) {
    return null;
  }

  // lengths[i * width + j] is the common subsequence length of a[start + i, endA) and b[start + j, endB).
  const width = m + 1;
  const lengths = new Uint32Array((n + 1) * width);
  for (let i = n - 1; i >= 0; i--) {
    for (let j = m - 1; j >= 0; j--) {
      lengths[i * width + j] = a[start + i] === b[start + j]
        ? lengths[(i + 1) * width + j + 1] + 1
        : Math.max(lengths[(i + 1) * width + j], lengths[i * width + j + 1]);
    }
  }

  const parts: DiffPart[] = [];
  if (start > 0) {
    append(parts, "same", a.slice(0, start).join(""));
  }

  let i = 0;
  let j = 0;
  while (i < n && j < m) {
    if (a[start + i] === b[start + j]) {
      append(parts, "same", a[start + i]);
      i++;
      j++;
    } else if (lengths[(i + 1) * width + j] >= lengths[i * width + j + 1]) {
      append(parts, "removed", a[start + i]);
      i++;
    } else {
      append(parts, "added", b[start + j]);
      j++;
    }
  }

  for (; i < n; i++) {
    append(parts, "removed", a[start + i]);
  }

  for (; j < m; j++) {
    append(parts, "added", b[start + j]);
  }

  if (endA < a.length) {
    append(parts, "same", a.slice(endA).join(""));
  }

  return tidy(parts);
}

/**
 * A diff with long unchanged stretches cut down to the words either side of each edit, so the edits of a long
 * description stay in view. `condensed` says whether anything was cut, which is when a reader needs a way to the whole.
 */
export function condenseDiff(parts: DiffPart[], context: number): { parts: DiffPart[]; condensed: boolean } {
  let condensed = false;
  const out = parts.map((part, index): DiffPart => {
    if (part.kind !== "same") {
      return part;
    }

    const words = tokens(part.text);
    const first = index === 0;
    const last = index === parts.length - 1;
    if (first && last) {
      return part;
    }

    const keep = first || last ? context : context * 2;
    if (words.length <= keep + 2) {
      return part;
    }

    condensed = true;
    if (first) {
      return { kind: "same", text: `…${words.slice(-context).join("")}` };
    }

    if (last) {
      return { kind: "same", text: `${words.slice(0, context).join("")}…` };
    }

    return { kind: "same", text: `${words.slice(0, context).join("")} … ${words.slice(-context).join("")}` };
  });
  return { parts: out, condensed };
}

/** One entry of a list compared as a set, such as a kind a variable points to. */
export interface ListEntryDiff {
  value: string;
  kind: DiffPart["kind"];
}

/** Two joined lists compared entry by entry, in order: the ones both hold, the ones only the older holds, the new ones. */
export function diffList(before: string | null, after: string | null, separator: string): ListEntryDiff[] {
  const split = (text: string | null) => (text === null || text === "" ? [] : text.split(separator));
  const older = new Set(split(before));
  const newer = new Set(split(after));
  return [...new Set([...older, ...newer])]
    .sort((x, y) => (x < y ? -1 : x > y ? 1 : 0))
    .map((value) => ({ value, kind: older.has(value) ? (newer.has(value) ? "same" : "removed") : "added" }));
}
