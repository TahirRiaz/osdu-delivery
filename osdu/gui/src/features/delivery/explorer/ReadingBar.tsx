/**
 * A read from OSDU under way, as a slim sweep along the top edge of the pane it fills: the workbench's own loading bar
 * (DESIGN.md 8.4), pinned to the pane rather than the window, so what is already shown stays readable while it is read again.
 */
export function ReadingBar({ label }: { label: string }) {
  return (
    <div className="pointer-events-none absolute inset-x-0 top-0 z-20 h-0.5 overflow-hidden" role="progressbar" aria-label={label} data-testid="explorer-reading">
      <div className="h-full w-2/5 animate-[top-progress_1.2s_ease-in-out_infinite] rounded-r bg-primary" />
    </div>
  );
}
