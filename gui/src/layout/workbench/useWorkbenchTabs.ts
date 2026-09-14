import { createContext, useContext, useEffect } from "react";
import { useLocation } from "react-router-dom";

export interface WorkbenchTab {
  /** The tab's identity: one tab per pathname (query-string changes update the same tab). */
  path: string;
  /** The full URL (path + search) last seen for this tab, restored on activation. */
  url: string;
  title: string;
}

export interface TabsValue {
  tabs: WorkbenchTab[];
  /** The pathname of the routed page, i.e. the active tab. */
  activePath: string;
  activate: (tab: WorkbenchTab) => void;
  close: (path: string) => void;
  /** Closes every tab except the given one. */
  closeOthers: (path: string) => void;
  /** Closes every tab sitting to the left of the given one. */
  closeToLeft: (path: string) => void;
  /** Closes every tab sitting to the right of the given one. */
  closeToRight: (path: string) => void;
  closeAll: () => void;
  setTitle: (path: string, title: string) => void;
}

export const TabsContext = createContext<TabsValue | null>(null);

export function useWorkbenchTabs(): TabsValue {
  const context = useContext(TabsContext);
  if (!context) {
    throw new Error("useWorkbenchTabs must be used inside TabsProvider.");
  }

  return context;
}

/** Reported by detail pages once their data loads, so the tab reads "orders_ods" instead of "Pipeline". */
export function useTabTitle(title: string | undefined): void {
  const { setTitle } = useWorkbenchTabs();
  const { pathname } = useLocation();

  useEffect(() => {
    if (title !== undefined && title !== "") {
      setTitle(pathname, title);
    }
  }, [title, pathname, setTitle]);
}
