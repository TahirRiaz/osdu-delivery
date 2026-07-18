import type { ReactNode } from "react";
import CallSplitIcon from "@mui/icons-material/CallSplit";
import FlashOnOutlinedIcon from "@mui/icons-material/FlashOnOutlined";
import FunctionsIcon from "@mui/icons-material/Functions";
import HelpOutlineIcon from "@mui/icons-material/HelpOutline";
import InsertDriveFileOutlinedIcon from "@mui/icons-material/InsertDriveFileOutlined";
import LayersOutlinedIcon from "@mui/icons-material/LayersOutlined";
import ShortcutIcon from "@mui/icons-material/Shortcut";
import TableRowsOutlinedIcon from "@mui/icons-material/TableRowsOutlined";

/** Display metadata for one object kind: the folder label the tree shows and the leaf icon. */
export interface KindMeta {
  /** The kind value as the catalog stores it (CatalogObject.Kind). */
  kind: string;
  /** The plural folder label ("Tables", "Views", ...). */
  plural: string;
  icon: ReactNode;
}

/** Every known object kind in display order; kinds the catalog invents later fall back to `metaForKind`. */
export const KIND_ORDER: readonly KindMeta[] = [
  { kind: "Table", plural: "Tables", icon: <TableRowsOutlinedIcon fontSize="small" /> },
  { kind: "View", plural: "Views", icon: <LayersOutlinedIcon fontSize="small" /> },
  { kind: "Procedure", plural: "Procedures", icon: <CallSplitIcon fontSize="small" /> },
  { kind: "Function", plural: "Functions", icon: <FunctionsIcon fontSize="small" /> },
  { kind: "Trigger", plural: "Triggers", icon: <FlashOnOutlinedIcon fontSize="small" /> },
  { kind: "Synonym", plural: "Synonyms", icon: <ShortcutIcon fontSize="small" /> },
  { kind: "File", plural: "Files", icon: <InsertDriveFileOutlinedIcon fontSize="small" /> },
  { kind: "Unknown", plural: "Unknown", icon: <HelpOutlineIcon fontSize="small" /> },
];

const BY_KIND = new Map(KIND_ORDER.map((meta) => [meta.kind, meta]));

/** The display metadata for a kind, tolerating a value outside the known set (shown as-is with a neutral icon). */
export function metaForKind(kind: string): KindMeta {
  return BY_KIND.get(kind) ?? { kind, plural: kind, icon: <HelpOutlineIcon fontSize="small" /> };
}

/** Sorts kind values into the display order above; unknown kinds sort after the known ones, alphabetically. */
export function compareKinds(a: string, b: string): number {
  const ia = KIND_ORDER.findIndex((meta) => meta.kind === a);
  const ib = KIND_ORDER.findIndex((meta) => meta.kind === b);
  if (ia >= 0 && ib >= 0) {
    return ia - ib;
  }
  if (ia >= 0) {
    return -1;
  }
  if (ib >= 0) {
    return 1;
  }
  return a.localeCompare(b);
}
