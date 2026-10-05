import { BookMarked, Box, Database, File, FileStack, Package, Shapes, type LucideIcon } from "lucide-react";
import { cn } from "@/lib/utils";

/** The glyph of each group OSDU sorts its types into; a group not listed takes the plain box. */
const GROUPS: Record<string, LucideIcon> = {
  "master-data": Database,
  "reference-data": BookMarked,
  "work-product-component": FileStack,
  "work-product": Package,
  dataset: File,
  abstract: Shapes,
};

/**
 * A group of types as a glyph, the same wherever the explorer lists a group, a type or a record of it, so the eye finds
 * master data, reference data, work products and datasets apart before it reads them. Muted, since a group is a kind of
 * thing and not a status (DESIGN.md 3.2).
 */
export function GroupGlyph({ group, className }: { group: string; className?: string }) {
  const Glyph = GROUPS[group] ?? Box;
  return <Glyph className={cn("size-4 shrink-0 text-muted-foreground", className)} aria-hidden />;
}
