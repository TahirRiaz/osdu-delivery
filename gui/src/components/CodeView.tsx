import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import Editor, { type Monaco } from "@monaco-editor/react";
import type { editor } from "monaco-editor";
import Box from "@mui/material/Box";
import Stack from "@mui/material/Stack";
import Tooltip from "@mui/material/Tooltip";
import IconButton from "@mui/material/IconButton";
import AutoFixHighIcon from "@mui/icons-material/AutoFixHigh";
import ContentCopyIcon from "@mui/icons-material/ContentCopy";
import { useTheme } from "@mui/material/styles";
import { useSnackbar } from "notistack";
import { format as formatSql } from "sql-formatter";
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

/** Resolves a brand token (--sf-*) to its current value, honouring light/dark; falls back if unset. */
function cssVar(name: string, fallback: string): string {
  if (typeof document === "undefined") {
    return fallback;
  }
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

/**
 * Defines the editor theme from the app's brand tokens (branding.css is the single colour source, so the code
 * view matches every other surface). Read live via getComputedStyle so it reflects the active mode; re-defined
 * whenever the app toggles light/dark. Rule colours are hex without '#'; editor colours keep it.
 */
function defineSqlflowTheme(monaco: Monaco, mode: "light" | "dark"): void {
  const color = (name: string, fallback: string) => cssVar(name, fallback);
  const rule = (name: string, fallback: string) => color(name, fallback).replace("#", "");

  monaco.editor.defineTheme("sqlflow", {
    base: mode === "dark" ? "vs-dark" : "vs",
    inherit: true,
    rules: [
      { token: "keyword", foreground: rule("--sf-primary", "#2f6fce"), fontStyle: "bold" },
      { token: "operator", foreground: rule("--sf-text-secondary", "#4d5a6a") },
      { token: "type", foreground: rule("--sf-info", "#0a6aa3") },
      { token: "predefined", foreground: rule("--sf-info", "#0a6aa3") },
      { token: "string", foreground: rule("--sf-success", "#2e7d32") },
      { token: "number", foreground: rule("--sf-warning", "#9a7d0a") },
      { token: "comment", foreground: rule("--sf-text-secondary", "#4d5a6a"), fontStyle: "italic" },
      { token: "delimiter", foreground: rule("--sf-text-secondary", "#4d5a6a") },
      { token: "tag", foreground: rule("--sf-primary", "#2f6fce") },
      { token: "attribute.name", foreground: rule("--sf-info", "#0a6aa3") },
      // Semantic tokens from the flow-YAML analysis engine (see lib/lsp). These
      // carry census knowledge the YAML grammar cannot: a documented key, a key
      // the loader will ignore, and valid vs invalid enum values.
      { token: "property", foreground: rule("--sf-info", "#0a6aa3") },
      { token: "unknownKey", foreground: rule("--sf-warning", "#9a7d0a"), fontStyle: "italic" },
      { token: "enumMember", foreground: rule("--sf-success", "#2e7d32") },
      { token: "invalidValue", foreground: rule("--sf-error", "#c62828"), fontStyle: "underline" },
    ],
    colors: {
      "editor.background": color("--sf-paper", mode === "dark" ? "#182434" : "#ffffff"),
      "editor.foreground": color("--sf-text-primary", mode === "dark" ? "#eef3f9" : "#1d2733"),
      "editorLineNumber.foreground": color("--sf-text-secondary", "#4d5a6a"),
      "editorLineNumber.activeForeground": color("--sf-primary", "#2f6fce"),
      "editorCursor.foreground": color("--sf-primary", "#2f6fce"),
      "editorIndentGuide.background": color("--sf-divider", "#e2e8f0"),
      "editorGutter.background": color("--sf-paper", mode === "dark" ? "#182434" : "#ffffff"),
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
 * highlight, folding, and in-editor search, themed with the app's brand palette in both light and dark. Flow-YAML
 * language intelligence (hover docs, census colouring, diagnostics) is on by default for YAML. Read-only by
 * default (git is the authoring surface); the debugger opts into editable mode via `readOnly={false}` +
 * `onChange`. For SQL, a toolbar offers pretty-printing (on by default, since captured statements arrive as
 * unformatted single-line blobs) and copy-to-clipboard of whatever is currently shown.
 */
export function CodeView({
  value, language, height = 480, lsp = true, readOnly = true, onChange, "data-testid": testId,
}: CodeViewProps) {
  const theme = useTheme();
  const mode = theme.palette.mode;
  const monacoRef = useRef<Monaco | null>(null);
  const editorRef = useRef<editor.IStandaloneCodeEditor | null>(null);
  const { enqueueSnackbar } = useSnackbar();
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

  // The brand tokens change when the app toggles; re-define and re-apply so the editor tracks the theme.
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
      .then(() => enqueueSnackbar("Copied to clipboard", { variant: "success" }))
      .catch(() => enqueueSnackbar("Could not copy to clipboard", { variant: "error" }));
  }, [displayValue, enqueueSnackbar]);

  return (
    <Box
      data-testid={testId ?? "code-view"}
      sx={{ border: 1, borderColor: "divider", borderRadius: 1, overflow: "hidden" }}
    >
      <Stack
        direction="row"
        spacing={0.5}
        justifyContent="flex-end"
        alignItems="center"
        sx={{
          px: 0.5,
          py: 0.25,
          borderBottom: 1,
          borderColor: "divider",
          bgcolor: "action.hover",
        }}
      >
        {isSql && (
          <Tooltip title={formatSqlOn ? "Show original SQL" : "Format SQL"}>
            <IconButton
              size="small"
              color={formatSqlOn ? "primary" : "default"}
              aria-label={formatSqlOn ? "Show original SQL" : "Format SQL"}
              aria-pressed={formatSqlOn}
              onClick={() => setFormatSqlOn((on) => !on)}
              data-testid="code-view-format"
            >
              <AutoFixHighIcon fontSize="small" />
            </IconButton>
          </Tooltip>
        )}
        <Tooltip title="Copy">
          <IconButton
            size="small"
            aria-label="Copy to clipboard"
            onClick={handleCopy}
            data-testid="code-view-copy"
          >
            <ContentCopyIcon fontSize="small" />
          </IconButton>
        </Tooltip>
      </Stack>
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
          fontSize: 13,
          padding: { top: 12, bottom: 12 },
          renderLineHighlight: readOnly ? "none" : "line",
          smoothScrolling: true,
          "semanticHighlighting.enabled": true,
        }}
      />
    </Box>
  );
}
