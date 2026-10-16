import { HeadClippedText } from "./HeadClippedText";
import { splitKind } from "./templateFormat";

interface KindTextProps {
  kind: string;
}

/**
 * An OSDU kind on one line, in whatever width its cell leaves. The entity name and version, which are what tell two
 * kinds apart, stay in view; the authority, source and group before them give way first, clipping from the left.
 */
export function KindText({ kind }: KindTextProps) {
  const { prefix, entity, version } = splitKind(kind);
  return <HeadClippedText head={prefix} body={entity} tail={version} title="Kind" className="font-mono text-[12px]" />;
}
