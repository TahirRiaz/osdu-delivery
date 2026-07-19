import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import Editor, { type Monaco } from "@monaco-editor/react";
import type { editor } from "monaco-editor";
import { Copy, WandSparkles } from "lucide-react";
import { toast } from "sonner";
import { format as formatSql } from "sql-formatter";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { useThemeMode } from "../theme/ThemeModeContext";
import "../lib/monacoSetup";
import { markFlowModel, refreshDiagnostics, registerSqlflowYamlProviders } from "../lib/lsp/sqlflowLsp";

interface CodeViewProps {
  value: string;
  language: "yaml" | "json" | "sql" | "plaintext";
  height?: number | string;
  /**
   * SQLFlow flow-YAML language intelligence (hover docs per attribute, census-driven colouring, and validation
   * squiggles), served by the wasm analysis engine. ON by default for `language: "yaml"` (every YAML shown in the
   * product is a flow document); pass false for a YAML surface that is not a flow document.
   */
  lsp?: boolean;
  /**
   * Editable mode: the editor accepts typing and reports changes through `onChange` (the debugger's YAML input).
   * Defaults to read-only, the mode every catalog/code viewer uses (git is the authoring surface).
   */
  readOnly?: boolean;
  onChange?: (value: string) => void;
  "data-testid"?: string;
}

/** Resolves a design token to its current value, honouring light/dark; falls back if unset. */
function cssVar(name: string, fallback: string): string {
  if (typeof document === "undefined") {
    return fallback;
  }
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

/**
 * Defines the editor theme from the workbench tokens (index.css is the single colour source, so the code
 * view matches every other surface; DESIGN.md 7.6). Read live via getComputedStyle so it reflects the
 * active mode; re-defined whenever the app toggles light/dark. Rule colours are hex without '#'; editor
 * colours keep it.
 */
function defineSqlflowTheme(monaco: Monaco, mode: "light" | "dark"): void {
  const color = (name: string, fallback: string) => cssVar(name, fallback);
  const rule = (name: string, fallback: string) => color(name, fallback).replace("#", "");

  monaco.editor.defineTheme("sqlflow", {
    base: mode === "dark" ? "vs-dark" : "vs",
    inherit: true,
    rules: [
      { token: "keyword", foreground: rule("--primary", "#2f6fce"), fontStyle: "bold" },
      { token: "operator", foreground: rule("--muted-foreground", "#5b6b7f") },
      { token: "type", foreground: rule("--info", "#0969da") },
      { token: "predefined", foreground: rule("--info", "#0969da") },
      { token: "string", foreground: rule("--success", "#1a7f37") },
      { token: "number", foreground: rule("--warning", "#9a6700") },
      { token: "comment", foreground: rule("--muted-foreground", "#5b6b7f"), fontStyle: "italic" },
      { token: "delimiter", foreground: rule("--muted-foreground", "#5b6b7f") },
      { token: "tag", foreground: rule("--primary", "#2f6fce") },
      { token: "attribute.name", foreground: rule("--info", "#0969da") },
      // Semantic tokens from the flow-YAML analysis engine (see lib/lsp). These
      // carry census knowledge the YAML grammar cannot: a documented key, a key
      // the loader will ignore, and valid vs invalid enum values.
      { token: "property", foreground: rule("--info", "#0969da") },
      { token: "unknownKey", foreground: rule("--warning", "#9a6700"), fontStyle: "italic" },
      { token: "enumMember", foreground: rule("--success", "#1a7f37") },
      { token: "invalidValue", foreground: rule("--destructive", "#d1242f"), fontStyle: "underline" },
    ],
    colors: {
      "editor.background": color("--card", mode === "dark" ? "#16202f" : "#ffffff"),
      "editor.foreground": color("--foreground", mode === "dark" ? "#dce6f2" : "#1d2733"),
      "editorLineNumber.foreground": color("--muted-foreground", "#5b6b7f"),
      "editorLineNumber.activeForeground": color("--primary", "#2f6fce"),
      "editorCursor.foreground": color("--primary", "#2f6fce"),
      "editorIndentGuide.background": color("--border", "#dfe5ee"),
      "editorGutter.background": color("--card", mode === "dark" ? "#16202f" : "#ffffff"),
      "editor.lineHighlightBorder": "#00000000",
    },
  });
}

/**
 * Pretty-prints captured T-SQL for display. The control plane executes against SQL Server, so captured
 * statements are Transact-SQL and are stored as single-line blobs (UPDATE/MERGE with long HASHBYTES/CONCAT
 * expressions). Formatting is best-effort: any input the parser rejects is returned unchanged so the viewer
 * always shows the real SQL rather than an error.
 */
function prettyPrintSql(sql: string): string {
  try {
    return formatSql(sql, {
      language: "transactsql",
      keywordCase: "upper",
      tabWidth: 2,
      linesBetweenQueries: 1,
    });
  } catch {
    return sql;
  }
}

/**
 * The Monaco surface for YAML documents, definition JSON, generated SQL, and plain-text trace payloads: syntax
 * highlight, folding, and in-editor search, themed with the workbench tokens in both light and dark. Flow-YAML
 * language intelligence (hover docs, census colouring, diagnostics) is on by default for YAML. Read-only by
 * default (git is the authoring surface); the debugger opts into editable mode via `readOnly={false}` +
 * `onChange`. For SQL, a toolbar offers pretty-printing (on by default, since captured statements arrive as
 * unformatted single-line blobs) and copy-to-clipboard of whatever is currently shown.
 */
export function CodeView({
  value, language, height = 480, lsp = true, readOnly = true, onChange, "data-testid": testId,
}: CodeViewProps) {
  const { mode } = useThemeMode();
  const monacoRef = useRef<Monaco | null>(null);
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null);
  const isSql = language === "sql";
  const lspOn = lsp && language === "yaml";
  const [formatSqlOn, setFormatSqlOn] = useState(true);

  const handleBeforeMount = useCallback((monaco: Monaco) => {
    monacoRef.current = monaco;
    defineSqlflowTheme(monaco, mode);
    if (lspOn) {
      registerSqlflowYamlProviders(monaco);
    }
  }, [mode, lspOn]);

  const handleMount = useCallback((editorInstance: editor.IStandaloneCodeEditor, monaco: Monaco) => {
    editorRef.current = editorInstance;
    if (!lspOn) {
      return;
    }
    const model = editorInstance.getModel();
    if (model) {
      markFlowModel(model);
      void refreshDiagnostics(monaco, model);
    }
  }, [lspOn]);

  // Re-validate when the document changes (navigating between flows swaps the
  // value on the same read-only model); semantic-token colouring re-pulls on its
  // own. On unmount, drop this model's markers.
  useEffect(() => {
    if (!lspOn) {
      return;
    }
    const monaco = monacoRef.current;
    const model = editorRef.current?.getModel();
    if (monaco && model) {
      markFlowModel(model);
      void refreshDiagnostics(monaco, model);
      return () => monaco.editor.setModelMarkers(model, "sqlflow", []);
    }
  }, [lspOn, value]);

  // The design tokens change when the app toggles; re-define and re-apply so the editor tracks the theme.
  useEffect(() => {
    if (monacoRef.current) {
      defineSqlflowTheme(monacoRef.current, mode);
      monacoRef.current.editor.setTheme("sqlflow");
    }
  }, [mode]);

  const displayValue = useMemo(
    () => (isSql && formatSqlOn ? prettyPrintSql(value) : value),
    [isSql, formatSqlOn, value],
  );

  const handleCopy = useCallback(() => {
    void navigator.clipboard
      .writeText(displayValue)
      .then(() => toast.success("Copied to clipboard"))
      .catch(() => toast.error("Could not copy to clipboard"));
  }, [displayValue]);

  return (
    <div
      data-testid={testId ?? "code-view"}
      className="overflow-hidden rounded-lg border border-border"
    >
      <div className="flex items-center justify-end gap-0.5 border-b border-border bg-muted/50 px-1 py-0.5">
        {isSql && (
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-xs"
                aria-label={formatSqlOn ? "Show original SQL" : "Format SQL"}
                aria-pressed={formatSqlOn}
                onClick={() => setFormatSqlOn((on) => !on)}
                data-testid="code-view-format"
                className={cn(formatSqlOn && "text-primary hover:text-primary")}
              >
                <WandSparkles />
              </Button>
            </TooltipTrigger>
            <TooltipContent>{formatSqlOn ? "Show original SQL" : "Format SQL"}</TooltipContent>
          </Tooltip>
        )}
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
        value={displayValue}
        language={language}
        height={height}
        beforeMount={handleBeforeMount}
        onMount={handleMount}
        onChange={readOnly || !onChange ? undefined : (text) => onChange(text ?? "")}
        theme="sqlflow"
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
          "semanticHighlighting.enabled": true,
        }}
      />
    </div>
  );
}
