import { createContext, useContext } from "react";

export interface TreeState {
  /** Every id that is currently expanded (the user's own expansion plus the auto-revealed chains). */
  expanded: ReadonlySet<string>;
  toggle: (id: string) => void;
  setOpen: (id: string, open: boolean) => void;
  selectedId: string | null;
  select: (id: string) => void;
}

export const TreeContext = createContext<TreeState | null>(null);

export function useTreeState(): TreeState {
  const tree = useContext(TreeContext);
  if (tree === null) {
    throw new Error("TreeNode rendered outside the catalog tree provider.");
  }
  return tree;
}
