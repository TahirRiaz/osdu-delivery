/**
 * The part of an id that tells it apart. Run and submission ids are UUIDv7, whose first characters are a timestamp: a
 * run and the submission it created, or two runs a minute apart, share them. The last characters are random.
 */
export function idTail(id: string): string {
  return id.replace(/-/g, "").slice(-6).toLowerCase();
}

/**
 * An id where only a short form fits (a link, a chip, a tab title): its tail after an ellipsis, "…1a2b3c", so it reads as
 * the end of something longer. The whole id stays with it on hover, in a copy, or in the link it opens.
 */
export function shortId(id: string): string {
  return `…${idTail(id)}`;
}
