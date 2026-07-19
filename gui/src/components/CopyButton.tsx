import { useEffect, useRef, useState } from "react";
import { Check, Copy } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

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

  const button = (
    <Button
      variant="ghost"
      size="sm"
      onClick={() => void copy()}
      data-testid={testId}
      className={state === "failed" ? "text-destructive hover:text-destructive" : undefined}
    >
      {state === "copied" ? <Check /> : <Copy />}
      {state === "copied" ? "Copied" : state === "failed" ? "Copy failed" : label}
    </Button>
  );

  if (state !== "failed") {
    return button;
  }

  return (
    <Tooltip>
      <TooltipTrigger asChild>{button}</TooltipTrigger>
      <TooltipContent>The browser denied clipboard access.</TooltipContent>
    </Tooltip>
  );
}
