import Editor from "@monaco-editor/react";
import Box from "@mui/material/Box";
import { useTheme } from "@mui/material/styles";
import "../lib/monacoSetup";

interface CodeViewProps {
  value: string;
  language: "yaml" | "json" | "sql";
  height?: number | string;
  "data-testid"?: string;
}

/**
 * Read-only Monaco view for YAML documents, definition JSON, and generated SQL: syntax highlight, folding, and
 * in-editor search, themed with the app. YAML stays read-only by design (git is the authoring surface).
 */
export function CodeView({ value, language, height = 480, "data-testid": testId }: CodeViewProps) {
  const theme = useTheme();
  return (
    <Box data-testid={testId ?? "code-view"} sx={{ border: 1, borderColor: "divider", borderRadius: 1, overflow: "hidden" }}>
      <Editor
        value={value}
        language={language}
        height={height}
        theme={theme.palette.mode === "dark" ? "vs-dark" : "light"}
        options={{
          readOnly: true,
          minimap: { enabled: false },
          scrollBeyondLastLine: false,
          wordWrap: "on",
          fontSize: 13,
          renderLineHighlight: "none",
        }}
      />
    </Box>
  );
}
