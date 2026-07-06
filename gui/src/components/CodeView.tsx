import { useCallback, useEffect, useRef } from "react";
import Editor, { type Monaco } from "@monaco-editor/react";
import Box from "@mui/material/Box";
import { useTheme } from "@mui/material/styles";
import "../lib/monacoSetup";

interface CodeViewProps {
  value: string;
  language: "yaml" | "json" | "sql";
  height?: number | string;
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
 * Read-only Monaco view for YAML documents, definition JSON, and generated SQL: syntax highlight, folding, and
 * in-editor search, themed with the app's brand palette in both light and dark. YAML stays read-only by design
 * (git is the authoring surface).
 */
export function CodeView({ value, language, height = 480, "data-testid": testId }: CodeViewProps) {
  const theme = useTheme();
  const mode = theme.palette.mode;
  const monacoRef = useRef<Monaco | null>(null);

  const handleBeforeMount = useCallback((monaco: Monaco) => {
    monacoRef.current = monaco;
    defineSqlflowTheme(monaco, mode);
  }, [mode]);

  // The brand tokens change when the app toggles; re-define and re-apply so the editor tracks the theme.
  useEffect(() => {
    if (monacoRef.current) {
      defineSqlflowTheme(monacoRef.current, mode);
      monacoRef.current.editor.setTheme("sqlflow");
    }
  }, [mode]);

  return (
    <Box data-testid={testId ?? "code-view"} sx={{ border: 1, borderColor: "divider", borderRadius: 1, overflow: "hidden" }}>
      <Editor
        value={value}
        language={language}
        height={height}
        beforeMount={handleBeforeMount}
        theme="sqlflow"
        options={{
          readOnly: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          wordWrap: "on",
          fontSize: 13,
          padding: { top: 12, bottom: 12 },
          renderLineHighlight: "none",
          smoothScrolling: true,
        }}
      />
    </Box>
  );
}
