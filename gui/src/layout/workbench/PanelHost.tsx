import { X } from "lucide-react";
import { usePanel } from "./PanelContext";

/**
 * The bottom panel surface (DESIGN.md section 6): an uppercase title header with a close button, the
 * feature-provided content scrolling beneath. Rendered only while a feature has content open.
 */
export function PanelHost() {
  const { content, close } = usePanel();
  if (content === null) {
    return null;
  }

  return (
    <section className="flex h-full flex-col bg-panel" aria-label={content.title}>
      <header className="flex h-8 shrink-0 items-center justify-between border-b border-border px-3">
        <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
          {content.title}
        </span>
        <button
          onClick={close}
          aria-label="Close panel"
          className="rounded-sm p-1 text-muted-foreground hover:bg-muted hover:text-foreground"
        >
          <X className="size-3.5" />
        </button>
      </header>
      <div className="min-h-0 flex-1 overflow-auto">{content.node}</div>
    </section>
  );
}
