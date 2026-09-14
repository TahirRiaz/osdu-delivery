import { useCallback, useEffect, useRef } from "react";
import { DiffEditor, type Monaco } from "@monaco-editor/react";
import { defineSqlflowTheme, sqlflowEditorTheme } from "@/lib/monacoTheme";
import { useThemeMode } from "../theme/useThemeMode";
import "../lib/monacoSetup";

interface CodeDiffViewProps {
  original: string;
  modified: string;
  /** What the left side is, shown above it. */
  originalLabel: string;
  /** What the right side is, shown above it. */
  modifiedLabel: string;
  language: "yaml" | "json" | "plaintext";
  height?: number | string;
  "data-testid"?: string;
}

/**
 * Two documents side by side in Monaco's diff editor, themed like {@link CodeView}: changed lines marked on both sides,
 * unchanged stretches folded away so the differences are what is in view, and nothing editable.
 */
export function CodeDiffView({
  original, modified, originalLabel, modifiedLabel, language, height = 560, "data-testid": testId,
}: CodeDiffViewProps) {
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

  return (
    <div data-testid={testId ?? "code-diff-view"} className="overflow-hidden rounded-lg border border-border">
      <div className="grid grid-cols-2 border-b border-border bg-muted/50 font-mono text-[11px] text-muted-foreground">
        <div className="truncate px-3 py-1" data-testid={`${testId ?? "code-diff-view"}-original`}>{originalLabel}</div>
        <div className="truncate border-l border-border px-3 py-1" data-testid={`${testId ?? "code-diff-view"}-modified`}>{modifiedLabel}</div>
      </div>
      <DiffEditor
        original={original}
        modified={modified}
        language={language}
        height={height}
        beforeMount={handleBeforeMount}
        theme={sqlflowEditorTheme}
        options={{
          readOnly: true,
          originalEditable: false,
          renderSideBySide: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          fontSize: 12,
          fontFamily: "'JetBrains Mono Variable', 'JetBrains Mono', Consolas, monospace",
          ignoreTrimWhitespace: false,
          renderOverviewRuler: true,
          hideUnchangedRegions: { enabled: true, contextLineCount: 3, minimumLineCount: 6, revealLineCount: 20 },
          smoothScrolling: true,
        }}
      />
    </div>
  );
}
