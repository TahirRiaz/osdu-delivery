import { useEffect, useRef, useState } from "react";
import Button from "@mui/material/Button";
import Tooltip from "@mui/material/Tooltip";
import CheckIcon from "@mui/icons-material/Check";
import ContentCopyIcon from "@mui/icons-material/ContentCopy";

interface CopyButtonProps {
  label: string;
  /** The text to copy, or a producer for it when building the text eagerly on every render would be wasteful. */
  text: string | (() => string);
  testId: string;
}

/**
 * A copy-to-clipboard button with inline success feedback. Clipboard access can be denied (an http origin, a
 * restrictive browser policy), so a failure surfaces as feedback on the button instead of silently doing
 * nothing.
 */
export function CopyButton({ label, text, testId }: CopyButtonProps) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => () => {
    if (timer.current !== null) {
      clearTimeout(timer.current);
    }
  }, []);

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(typeof text === "function" ? text() : text);
      setState("copied");
    } catch {
      setState("failed");
    }

    if (timer.current !== null) {
      clearTimeout(timer.current);
    }

    timer.current = setTimeout(() => setState("idle"), 1800);
  };

  return (
    <Tooltip title={state === "failed" ? "The browser denied clipboard access." : ""}>
      <Button
        size="small"
        color={state === "failed" ? "error" : "inherit"}
        startIcon={state === "copied" ? <CheckIcon /> : <ContentCopyIcon />}
        onClick={() => void copy()}
        data-testid={testId}
      >
        {state === "copied" ? "Copied" : state === "failed" ? "Copy failed" : label}
      </Button>
    </Tooltip>
  );
}
