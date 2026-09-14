import { useCallback, useMemo, useState, type ReactNode } from "react";
import { PanelContext, type PanelContent, type PanelValue } from "./usePanel";

/**
 * The workbench's bottom panel (the VS Code "Panel"): features open live surfaces here (a run's streaming
 * trace, say) without leaving their page. One panel, one content at a time; opening replaces what is shown.
 */
export function PanelProvider({ children }: { children: ReactNode }) {
  const [content, setContent] = useState<PanelContent | null>(null);

  const open = useCallback((next: PanelContent) => {
    setContent(next);
  }, []);

  const close = useCallback(() => {
    setContent(null);
  }, []);

  const value = useMemo<PanelValue>(() => ({ content, open, close }), [content, open, close]);

  return <PanelContext.Provider value={value}>{children}</PanelContext.Provider>;
}
