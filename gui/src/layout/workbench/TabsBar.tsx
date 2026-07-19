import { X } from "lucide-react";
import { cn } from "@/lib/utils";
import { routeTitle } from "../nav";
import { useWorkbenchTabs } from "./TabsContext";

/**
 * The VS Code-style tab strip over the router (DESIGN.md section 6): one tab per visited route, the
 * active tab wearing the editor surface and a top accent line. Middle-click or the X closes a tab.
 */
export function TabsBar() {
  const { tabs, activePath, activate, close } = useWorkbenchTabs();

  return (
    <div
      role="tablist"
      aria-label="Open pages"
      className="flex h-[35px] shrink-0 items-stretch overflow-x-auto border-b border-border bg-tab-bar [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
    >
      {tabs.map((tab) => {
        const { icon: Icon } = routeTitle(tab.path);
        const isActive = tab.path === activePath;
        return (
          <div
            key={tab.path}
            role="tab"
            aria-selected={isActive}
            tabIndex={0}
            onClick={() => activate(tab)}
            onKeyDown={(event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                activate(tab);
              }
            }}
            onAuxClick={(event) => {
              if (event.button === 1) {
                close(tab.path);
              }
            }}
            className={cn(
              "group relative flex min-w-0 max-w-52 cursor-pointer select-none items-center gap-1.5 border-r border-border/60 px-3 text-xs",
              isActive ? "bg-tab-active text-foreground" : "text-muted-foreground hover:bg-tab-active/50",
            )}
          >
            {isActive && <span className="absolute inset-x-0 top-0 h-px bg-primary" />}
            <Icon className="size-3.5 shrink-0 opacity-70" />
            <span className="truncate">{tab.title}</span>
            <button
              aria-label={`Close ${tab.title}`}
              onClick={(event) => {
                event.stopPropagation();
                close(tab.path);
              }}
              className={cn(
                "ml-0.5 shrink-0 rounded-sm p-0.5 hover:bg-muted",
                isActive ? "opacity-70 hover:opacity-100" : "opacity-0 group-hover:opacity-70",
              )}
            >
              <X className="size-3.5" />
            </button>
          </div>
        );
      })}
    </div>
  );
}
