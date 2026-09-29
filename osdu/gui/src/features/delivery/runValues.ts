// How the trigger dialog's fields read what an operator types: lines of names, and name=value pairs for a flow's parameters.

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** The non-blank, trimmed lines of a textarea. */
export function lines(text: string): string[] {
  return text.split(/\r?\n/).map((line) => line.trim()).filter((line) => line !== "");
}

/** Parses "name=value" lines into the flow's parameter values; the first malformed line is the error. */
export function parseValues(text: string): { values: Record<string, string>; error: string | null } {
  const values: Record<string, string> = {};
  for (const line of lines(text)) {
    const at = line.indexOf("=");
    const name = at > 0 ? line.slice(0, at).trim() : "";
    if (at <= 0 || !IDENTIFIER.test(name)) {
      return { values, error: `Parameter '${line}' must be written as name=value (the name an identifier).` };
    }

    values[name] = line.slice(at + 1);
  }

  return { values, error: null };
}
