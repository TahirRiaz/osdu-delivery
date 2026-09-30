import { RichTooltip } from "@/components/RichTooltip";
import { useClipped } from "@/components/useClipped";
import { cn } from "@/lib/utils";

/** One run of a value as it draws: plain text, a stretch the search matched, or a character that would not show. */
interface Piece {
  text: string;
  match: boolean;
  /** Stands for a character a reader could not see (a space at an end, a tab, a line break, a control character). */
  shown: boolean;
}

/** The marks that stand for characters a reader could not see, each a glyph that is not itself a common value. */
function visible(char: string, atEdge: boolean): string | null {
  switch (char) {
    case " ":
      return atEdge ? "·" : null;
    case "\t":
      return "⇥";
    case "\n":
    case "\r":
      return "↵";
    case " ":
      return "⍽";
    default: {
      const code = char.codePointAt(0) ?? 0;
      // Control characters and the zero-width ones (joiners, the byte order mark) draw as their code point.
      return code < 0x20 || (code >= 0x7f && code < 0xa0) || (code >= 0x200b && code <= 0x200f) || code === 0xfeff
        ? `\\u${code.toString(16).toUpperCase().padStart(4, "0")}`
        : null;
    }
  }
}

/**
 * A value cut into the pieces it draws as: every character a reader could not see given a visible mark (a space only at
 * either end, where it tells two originals apart that otherwise read the same), and the stretches the search matched
 * marked, regardless of case.
 */
function pieces(text: string, search: string): Piece[] {
  const chars = Array.from(text);
  const leading = chars.findIndex((char) => char !== " ");
  const firstBody = leading < 0 ? chars.length : leading;
  let lastBody = chars.length - 1;
  while (lastBody >= 0 && chars[lastBody] === " ") {
    lastBody--;
  }

  const term = search.trim().toLowerCase();
  const lower = chars.map((char) => char.toLowerCase());
  const matched = new Uint8Array(chars.length);
  if (term !== "") {
    const termChars = Array.from(term);
    for (let at = 0; at + termChars.length <= chars.length; at++) {
      if (termChars.every((char, offset) => lower[at + offset] === char)) {
        matched.fill(1, at, at + termChars.length);
        at += termChars.length - 1;
      }
    }
  }

  const result: Piece[] = [];
  chars.forEach((char, index) => {
    const mark = visible(char, index < firstBody || index > lastBody);
    const piece: Piece = { text: mark ?? char, match: matched[index] === 1, shown: mark !== null };
    const last = result.at(-1);
    if (last !== undefined && last.match === piece.match && last.shown === piece.shown) {
      last.text += piece.text;
    } else {
      result.push(piece);
    }
  });

  return result;
}

/** Whether a value holds a character that draws as a mark, so hovering it explains the marks. */
function hasMarks(text: string): boolean {
  return pieces(text, "").some((piece) => piece.shown);
}

/**
 * A dimension's value exactly as the ledger keeps it: an original as the index holds it, or a member's clean value. What a
 * reader could not see is drawn as a mark in muted text (a middle dot for a space at either end, an arrow for a tab, a
 * return for a line break, the code point of a control character), since two originals that differ only there read as one
 * otherwise; an empty value says so. The search's matches are marked, the value clips at `maxWidth`, and it is whole on
 * hover when clipped or marked.
 */
export function DimensionValueText({ value, search = "", strong = false, maxWidth = 320, className, testId }: {
  value: string;
  search?: string;
  /** Draws the value as its row's identity. */
  strong?: boolean;
  maxWidth?: number;
  className?: string;
  testId?: string;
}) {
  const [ref, clipped] = useClipped(value);
  if (value === "") {
    return <span className="font-mono text-[12px] italic text-muted-foreground" data-testid={testId}>(empty)</span>;
  }

  const drawn = pieces(value, search);
  const body = (
    <span
      ref={ref}
      className={cn("inline-block truncate align-bottom font-mono text-[12px]", strong ? "font-medium text-foreground" : undefined, className)}
      style={{ maxWidth }}
      data-testid={testId}
      data-value={value}
    >
      {drawn.map((piece, index) => (
        <span
          // The pieces of one value never reorder, so their place is their identity.
          key={index}
          className={cn(piece.shown && "text-muted-foreground/70", piece.match && "rounded-[2px] bg-warning/25 text-foreground")}
        >
          {piece.text}
        </span>
      ))}
    </span>
  );

  const marked = hasMarks(value);
  if (!clipped && !marked) {
    return body;
  }

  const note = marked
    ? "\n\nMarked: · a space at either end, ⇥ a tab, ↵ a line break, ⍽ a non-breaking space, \\uXXXX a character that does not print."
    : "";
  return <RichTooltip title="Exact value" body={`${JSON.stringify(value)}${note}`} mono>{body}</RichTooltip>;
}
