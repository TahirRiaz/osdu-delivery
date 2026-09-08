import { useCallback, useEffect, useRef } from "react";
import Editor, { type Monaco } from "@monaco-editor/react";
import { Copy } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { defineSqlflowTheme, sqlflowEditorTheme } from "@/lib/monacoTheme";
import { useThemeMode } from "../theme/ThemeModeContext";
import "../lib/monacoSetup";

interface CodeViewProps {
  value: string;
  language: "yaml" | "json" | "plaintext";
  height?: number | string;
  /**
   * Editable mode: the editor accepts typing and reports changes through `onChange`. Defaults to read-only, the
   * mode every catalog viewer uses (git is the authoring surface).
   */
  readOnly?: boolean;
  onChange?: (value: string) => void;
  "data-testid"?: string;
}

/**
 * The Monaco surface for YAML documents, definition JSON, and plain-text trace payloads: syntax highlight,
 * folding, and in-editor search, themed with the workbench tokens in both light and dark. Read-only by default
 * (git is the authoring surface); a caller opts into editable mode via `readOnly={false}` + `onChange`. The
 * toolbar offers copy-to-clipboard of whatever is currently shown.
 */
export function CodeView({
  value, language, height = 480, readOnly = true, onChange, "data-testid": testId,
}: CodeViewProps) {
  const { mode } = useThemeMode();
  const monacoRef = useRef<Monaco | null>(null);

  const handleBeforeMount = useCallback((monaco: Monaco) => {
    monacoRef.current = monaco;
    defineSqlflowTheme(monaco, mode);
  }, [mode]);

  // The design tokens change when the app toggles; re-define and re-apply so the editor tracks the theme.
  useEffect(() => {
    if (monacoRef.current) {
      defineSqlflowTheme(monacoRef.current, mode);
      monacoRef.current.editor.setTheme(sqlflowEditorTheme);
    }
  }, [mode]);

  const handleCopy = useCallback(() => {
    void navigator.clipboard
      .writeText(value)
      .then(() => toast.success("Copied to clipboard"))
      .catch(() => toast.error("Could not copy to clipboard"));
  }, [value]);

  return (
    <div
      data-testid={testId ?? "code-view"}
      className="overflow-hidden rounded-lg border border-border"
    >
      <div className="flex items-center justify-end gap-0.5 border-b border-border bg-muted/50 px-1 py-0.5">
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon-xs"
              aria-label="Copy to clipboard"
              onClick={handleCopy}
              data-testid="code-view-copy"
            >
              <Copy />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Copy</TooltipContent>
        </Tooltip>
      </div>
      <Editor
        value={value}
        language={language}
        height={height}
        beforeMount={handleBeforeMount}
        onChange={readOnly || !onChange ? undefined : (text) => onChange(text ?? "")}
        theme={sqlflowEditorTheme}
        options={{
          readOnly,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          wordWrap: "on",
          fontSize: 12,
          fontFamily: "'JetBrains Mono Variable', 'JetBrains Mono', Consolas, monospace",
          padding: { top: 12, bottom: 12 },
          renderLineHighlight: readOnly ? "none" : "line",
          smoothScrolling: true,
        }}
      />
    </div>
  );
}
