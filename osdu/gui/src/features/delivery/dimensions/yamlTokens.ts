// How a YAML line of a flow is drawn, character by character: its keys, values, punctuation and comments, as the blocks
// that show a dimension's or a view's item of its flow tell them apart.

/** How a character of a line is drawn: a key, a value, punctuation or a comment. */
export type Token = "key" | "value" | "mark" | "comment" | "space";

/** The kind of each character of a YAML line, as a reader tells them apart. */
export function tokensOf(line: string): Token[] {
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

export const TOKEN_CLASS: Record<Token, string> = {
  key: "text-primary",
  value: "text-foreground",
  mark: "text-muted-foreground",
  comment: "italic text-muted-foreground/80",
  space: "",
};
