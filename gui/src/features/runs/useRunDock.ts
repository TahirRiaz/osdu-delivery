import { createContext, useContext } from "react";

export interface RunDockApi {
  /** The tracked group ids, oldest first. */
  groupIds: string[];
  minimized: boolean;
  /** Start (or keep) watching a run group and pop the dock open, so a freshly launched run is immediately visible. */
  track: (groupId: string) => void;
  /** Stop watching a group (the user dismissed its chip, or it 404'd / finished and auto-expired). */
  untrack: (groupId: string) => void;
  setMinimized: (minimized: boolean) => void;
}

export const RunDockContext = createContext<RunDockApi | null>(null);

export function useRunDock(): RunDockApi {
  const ctx = useContext(RunDockContext);
  if (ctx === null) {
    throw new Error("useRunDock must be used inside <RunDockProvider>");
  }

  return ctx;
}
