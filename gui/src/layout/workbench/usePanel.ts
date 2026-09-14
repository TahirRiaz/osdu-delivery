import { createContext, useContext, type ReactNode } from "react";

export interface PanelContent {
  /** Stable identity so a feature can tell whether the panel currently shows its content. */
  id: string;
  title: string;
  node: ReactNode;
}

export interface PanelValue {
  content: PanelContent | null;
  open: (content: PanelContent) => void;
  close: () => void;
}

export const PanelContext = createContext<PanelValue | null>(null);

export function usePanel(): PanelValue {
  const context = useContext(PanelContext);
  if (!context) {
    throw new Error("usePanel must be used inside PanelProvider.");
  }

  return context;
}
