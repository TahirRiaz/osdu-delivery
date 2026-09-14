import {
  useCallback,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { routeTitle } from "../nav";
import { TabsContext, type TabsValue, type WorkbenchTab } from "./useWorkbenchTabs";

const HOME_PATH = "/";

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
  const [routedUrl, setRoutedUrl] = useState<string | null>(null);

  // The routed location is the source of truth: it opens new tabs and refreshes the active tab's URL.
  const locationPath = location.pathname;
  const locationUrl = locationPath + location.search;
  if (routedUrl !== locationUrl) {
    setRoutedUrl(locationUrl);
    setTabs((current) => {
      const index = current.findIndex((tab) => tab.path === locationPath);
      if (index >= 0) {
        if (current[index].url === locationUrl) {
          return current;
        }

        const next = [...current];
        next[index] = { ...next[index], url: locationUrl };
        return next;
      }

      return [...current, { path: locationPath, url: locationUrl, title: routeTitle(locationPath).title }];
    });
  }

  useEffect(() => {
    window.sessionStorage.setItem(STORAGE_KEY, JSON.stringify(tabs));
  }, [tabs]);

  const activate = useCallback((tab: WorkbenchTab) => {
    navigate(tab.url);
  }, [navigate]);

  /**
   * The one closing path: drops every tab the predicate selects, then keeps the routed page valid. Closing
   * the active tab focuses the nearest survivor to its right, else the one to its left; emptying the strip
   * falls back to the dashboard, so there is never a routed page without a tab.
   */
  const closeTabs = useCallback((shouldClose: (tab: WorkbenchTab, index: number) => boolean) => {
    const survivors = tabs.filter((tab, index) => !shouldClose(tab, index));
    if (survivors.length === tabs.length) {
      return;
    }

    if (survivors.length === 0) {
      const home: WorkbenchTab = { path: HOME_PATH, url: HOME_PATH, title: routeTitle(HOME_PATH).title };
      setTabs([home]);
      navigate(home.url);
      return;
    }

    setTabs(survivors);

    const activeIndex = tabs.findIndex((tab) => tab.path === location.pathname);
    if (activeIndex < 0 || !shouldClose(tabs[activeIndex], activeIndex)) {
      return;
    }

    const survivorsBefore = tabs
      .slice(0, activeIndex)
      .filter((tab, index) => !shouldClose(tab, index))
      .length;
    navigate(survivors[Math.min(survivorsBefore, survivors.length - 1)].url);
  }, [tabs, location.pathname, navigate]);

  const close = useCallback((path: string) => {
    closeTabs((tab) => tab.path === path);
  }, [closeTabs]);

  const closeOthers = useCallback((path: string) => {
    closeTabs((tab) => tab.path !== path);
  }, [closeTabs]);

  const closeToLeft = useCallback((path: string) => {
    const index = tabs.findIndex((tab) => tab.path === path);
    if (index < 0) {
      return;
    }

    closeTabs((_tab, position) => position < index);
  }, [tabs, closeTabs]);

  const closeToRight = useCallback((path: string) => {
    const index = tabs.findIndex((tab) => tab.path === path);
    if (index < 0) {
      return;
    }

    closeTabs((_tab, position) => position > index);
  }, [tabs, closeTabs]);

  const closeAll = useCallback(() => {
    closeTabs(() => true);
  }, [closeTabs]);

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
    closeOthers,
    closeToLeft,
    closeToRight,
    closeAll,
    setTitle,
  }), [tabs, location.pathname, activate, close, closeOthers, closeToLeft, closeToRight, closeAll, setTitle]);

  return <TabsContext.Provider value={value}>{children}</TabsContext.Provider>;
}
