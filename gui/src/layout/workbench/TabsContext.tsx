import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { routeTitle } from "../nav";

export interface WorkbenchTab {
  /** The tab's identity: one tab per pathname (query-string changes update the same tab). */
  path: string;
  /** The full URL (path + search) last seen for this tab, restored on activation. */
  url: string;
  title: string;
}

interface TabsValue {
  tabs: WorkbenchTab[];
  /** The pathname of the routed page, i.e. the active tab. */
  activePath: string;
  activate: (tab: WorkbenchTab) => void;
  close: (path: string) => void;
  setTitle: (path: string, title: string) => void;
}

const TabsContext = createContext<TabsValue | null>(null);

const STORAGE_KEY = "sqlflow.workbench.tabs";

function loadStoredTabs(): WorkbenchTab[] {
  try {
    const raw = window.sessionStorage.getItem(STORAGE_KEY);
    if (raw === null) {
      return [];
    }

    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed.filter((entry): entry is WorkbenchTab =>
      typeof entry === "object" && entry !== null
      && typeof (entry as WorkbenchTab).path === "string"
      && typeof (entry as WorkbenchTab).url === "string"
      && typeof (entry as WorkbenchTab).title === "string");
  } catch {
    // A corrupt store (manual edit, quota weirdness) just means starting with no restored tabs.
    return [];
  }
}

/**
 * VS Code-style editor tabs over the router: every visited route opens (or re-activates) a tab, closing a tab
 * navigates to its neighbor, and the set survives a refresh via sessionStorage. Detail pages refine their
 * tab's title through useTabTitle once they know the entity they show.
 */
export function TabsProvider({ children }: { children: ReactNode }) {
  const location = useLocation();
  const navigate = useNavigate();
  const [tabs, setTabs] = useState<WorkbenchTab[]>(loadStoredTabs);

  // The routed location is the source of truth: it opens new tabs and refreshes the active tab's URL.
  useEffect(() => {
    const path = location.pathname;
    const url = path + location.search;
    setTabs((current) => {
      const index = current.findIndex((tab) => tab.path === path);
      if (index >= 0) {
        if (current[index].url === url) {
          return current;
        }

        const next = [...current];
        next[index] = { ...next[index], url };
        return next;
      }

      return [...current, { path, url, title: routeTitle(path).title }];
    });
  }, [location.pathname, location.search]);

  useEffect(() => {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(tabs));
  }, [tabs]);

  const activate = useCallback((tab: WorkbenchTab) => {
    navigate(tab.url);
  }, [navigate]);

  const close = useCallback((path: string) => {
    const index = tabs.findIndex((tab) => tab.path === path);
    if (index < 0) {
      return;
    }

    const next = tabs.filter((tab) => tab.path !== path);
    setTabs(next);
    if (path === location.pathname) {
      // Closing the active tab focuses its right neighbor, else the left one, else the dashboard (which
      // re-opens as a fresh tab when none remain).
      const target = next[Math.min(index, next.length - 1)];
      navigate(target !== undefined ? target.url : "/");
    }
  }, [tabs, location.pathname, navigate]);

  const setTitle = useCallback((path: string, title: string) => {
    setTabs((current) => {
      const index = current.findIndex((tab) => tab.path === path);
      if (index < 0 || current[index].title === title) {
        return current;
      }

      const next = [...current];
      next[index] = { ...next[index], title };
      return next;
    });
  }, []);

  const value = useMemo<TabsValue>(() => ({
    tabs,
    activePath: location.pathname,
    activate,
    close,
    setTitle,
  }), [tabs, location.pathname, activate, close, setTitle]);

  return <TabsContext.Provider value={value}>{children}</TabsContext.Provider>;
}

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
