import { useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Eye, Loader2, Save, X } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Textarea } from "@/components/ui/textarea";
import { deliveryApi, type DeliveryTemplateDetail, type DeliveryTemplateSaved } from "../../api/delivery";
import { originText } from "./TemplateHeader";
import { ProblemView, problemText, TemplateSheet } from "./TemplateSheet";

/** The largest schema file the page reads. A bundled OSDU schema runs to a few hundred kilobytes. */
const MaxSchemaMegabytes = 10;

/** A schema laid out for a look: what was laid out, where it came from, the release its references were read from, and the template it gave. */
interface Shown {
  kind: string;
  schema: Record<string, unknown>;
  origin: string;
  /** The release of the OSDU data definitions the schema's references are read from; null for a bundled schema. */
  release: string | null;
  detail: DeliveryTemplateDetail;
}

/** What schema text says about itself before anything is sent: the kind it names, and the files outside itself it refers to. */
interface Inspected {
  kind: string | null;
  outside: string[];
}

const NothingInspected: Inspected = { kind: null, outside: [] };

/**
 * The kind a schema says it describes (its top-level x-osdu-schema-source), and every reference that is not to a definition
 * inside it, as a file the OSDU data definitions publish refers to the shared schemas beside it.
 */
function inspect(text: string): Inspected {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    // Text that is not JSON says nothing about itself. Preview reports what is wrong with it when it is asked to lay it out.
    return NothingInspected;
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return NothingInspected;
  }

  const outside = new Set<string>();
  const walk = (node: unknown) => {
    if (Array.isArray(node)) {
      node.forEach(walk);
      return;
    }

    if (typeof node !== "object" || node === null) {
      return;
    }

    for (const [key, value] of Object.entries(node)) {
      if (key === "$ref" && typeof value === "string") {
        if (!value.startsWith("#")) {
          outside.add(value);
        }
      } else {
        walk(value);
      }
    }
  };
  walk(parsed);

  const source = (parsed as Record<string, unknown>)["x-osdu-schema-source"];
  return { kind: typeof source === "string" && source.trim() !== "" ? source.trim() : null, outside: [...outside] };
}

/** Checks the kind and parses the schema text before anything is sent. The first problem found is the error. */
function readSchema(kind: string, text: string): { schema: Record<string, unknown> | null; error: string | null } {
  const trimmedKind = kind.trim();
  if (trimmedKind === "") {
    return { schema: null, error: "Give the kind the schema describes, such as osdu:wks:master-data--Wellbore:1.3.0." };
  }

  if (trimmedKind.split(":").length !== 4) {
    return { schema: null, error: "Write the kind as authority:source:entityType:version, such as osdu:wks:master-data--Wellbore:1.3.0." };
  }

  if (text.trim() === "") {
    return { schema: null, error: "Choose a schema file, or paste the schema JSON." };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch (error) {
    return { schema: null, error: `The schema is not valid JSON: ${error instanceof Error ? error.message : String(error)}` };
  }

  if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
    return { schema: null, error: "The schema must be a JSON object." };
  }

  return { schema: parsed as Record<string, unknown>, error: null };
}

/** The toast a save ends with: a new version, or one the catalog already held. */
function savedMessage(saved: DeliveryTemplateSaved): string {
  return saved.outcome === "created"
    ? `Saved template ${saved.template.kind} version ${saved.template.version}.`
    : `Template ${saved.template.kind} version ${saved.template.version} was already saved, so nothing changed.`;
}

/**
 * Import file: a schema file, or pasted schema JSON, laid out as a template and saved the same way as a schema browsed from
 * OSDU. A bundled schema is taken as it is; a file as the OSDU data definitions publish it has the shared schemas it refers
 * to read from a release and bundled in.
 */
export function TemplatesImportTab({ canAuthor }: { canAuthor: boolean }) {
  const queryClient = useQueryClient();
  const fileInput = useRef<HTMLInputElement>(null);
  // The kind typed in, asked for only when the schema does not name its own.
  const [typedKind, setTypedKind] = useState("");
  const [text, setText] = useState("");
  const [file, setFile] = useState<{ name: string; text: string } | null>(null);
  const [pickedRelease, setPickedRelease] = useState<string | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [reading, setReading] = useState(false);
  const [shown, setShown] = useState<Shown | null>(null);
  const [sheetOpen, setSheetOpen] = useState(false);

  const inspected = useMemo(() => inspect(text), [text]);
  const declared = inspected.kind;
  const kind = declared ?? typedKind;
  const outside = inspected.outside;

  // The releases are read only for a schema that refers to the shared schemas; it is bundled from the newest unless another is picked.
  const releases = useQuery({
    queryKey: ["delivery", "osdu-definitions", "releases"],
    queryFn: deliveryApi.osduReleases,
    staleTime: 10 * 60_000,
    enabled: outside.length > 0,
  });
  const release = outside.length === 0 ? null : pickedRelease ?? releases.data?.releases[0]?.name ?? null;
  const chosenRelease = releases.data?.releases.find((candidate) => candidate.name === release);

  const preview = useMutation({
    mutationFn: async (input: Omit<Shown, "detail">): Promise<Shown> =>
      ({ ...input, detail: await deliveryApi.previewTemplate(input.kind, input.schema, null, input.release) }),
    onSuccess: (result) => {
      save.reset();
      setShown(result);
      setSheetOpen(true);
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const save = useMutation({
    mutationFn: (input: Shown) => deliveryApi.saveTemplate(input.kind, input.schema, input.origin, input.release),
    onSuccess: (saved, input) => {
      toast.success(savedMessage(saved));
      // The sheet now shows the version as saved, with who saved it and from where.
      setShown({ ...input, detail: { ...input.detail, saved: saved.template } });
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
    onError: (error) => toast.error(problemText(error)),
  });

  // Save from the form: the schema is laid out as Preview lays it out, saved, and the sheet opens on the saved version.
  const saveNow = useMutation({
    mutationFn: async (input: Omit<Shown, "detail">): Promise<{ shown: Shown; saved: DeliveryTemplateSaved }> => {
      const detail = await deliveryApi.previewTemplate(input.kind, input.schema, null, input.release);
      const saved = await deliveryApi.saveTemplate(input.kind, input.schema, input.origin, input.release);
      return { shown: { ...input, detail: { ...detail, saved: saved.template } }, saved };
    },
    onSuccess: ({ shown: result, saved }) => {
      toast.success(savedMessage(saved));
      save.reset();
      preview.reset();
      setShown(result);
      setSheetOpen(true);
      void queryClient.invalidateQueries({ queryKey: ["delivery", "templates"] });
    },
    onError: (error) => toast.error(problemText(error)),
  });

  const choose = (chosen: File | undefined) => {
    if (chosen === undefined) {
      return;
    }

    if (chosen.size > MaxSchemaMegabytes * 1024 * 1024) {
      setProblem(`The file '${chosen.name}' is ${(chosen.size / 1024 / 1024).toFixed(1)} MB; a schema file is at most ${MaxSchemaMegabytes} MB.`);
      return;
    }

    setReading(true);
    setProblem(null);
    chosen.text()
      .then((content) => {
        setText(content);
        setFile({ name: chosen.name, text: content });
      })
      .catch((error: unknown) => {
        setProblem(`The file '${chosen.name}' could not be read: ${error instanceof Error ? error.message : String(error)}`);
      })
      .finally(() => {
        setReading(false);
        // Choosing the same file again after editing the text reads it afresh.
        if (fileInput.current !== null) {
          fileInput.current.value = "";
        }
      });
  };

  /** The form as a schema to send, or null after showing what is wrong with it. */
  const checked = (): Omit<Shown, "detail"> | null => {
    const { schema, error } = readSchema(kind, text);
    if (schema === null) {
      setProblem(error);
      return null;
    }

    if (outside.length > 0 && release === null) {
      setProblem(releases.isError
        ? "The schema refers to shared schemas of the OSDU data definitions, and the list of their releases could not be read."
        : "The schema refers to shared schemas of the OSDU data definitions; wait for the list of releases, which they are read from.");
      return null;
    }

    setProblem(null);
    // The origin names the file only while the text is still exactly what was read from it.
    const origin = file !== null && text === file.text ? `file ${file.name}` : "pasted schema";
    return { kind: kind.trim(), schema, origin, release };
  };

  const runPreview = () => {
    const input = checked();
    if (input !== null) {
      saveNow.reset();
      preview.mutate(input);
    }
  };

  const runSave = () => {
    const input = checked();
    if (input !== null) {
      preview.reset();
      saveNow.mutate(input);
    }
  };

  const clear = () => {
    setTypedKind("");
    setText("");
    setFile(null);
    setPickedRelease(null);
    setProblem(null);
    preview.reset();
    saveNow.reset();
  };

  const working = preview.isPending || saveNow.isPending || reading;
  const shownOrigin = shown === null
    ? null
    : shown.release === null ? shown.origin : `${shown.origin}, references from OSDU data definitions ${shown.release}`;

  return (
    <div className="flex flex-col gap-4" data-testid="templates-import">
      <Card className="gap-3 rounded-lg p-4" data-testid="templates-import-form">
        <p className="text-xs text-muted-foreground">
          A schema file as the OSDU data definitions publish it, or a bundled schema that holds every definition it refers to.
          The shared schemas a published file refers to are read from a release of the data definitions and bundled in, and
          either is laid out and saved exactly like a schema browsed from OSDU.
        </p>
        <div className="grid grid-cols-1 gap-3 md:grid-cols-2">
          {declared !== null ? (
            <div className="flex flex-col gap-1.5">
              <Label>Kind</Label>
              <p className="flex h-8 items-center break-all font-mono text-[13px]" data-testid="templates-import-kind-declared">{declared}</p>
              <p className="text-xs text-muted-foreground">
                Read from the schema&apos;s <span className="font-mono">x-osdu-schema-source</span>.
              </p>
            </div>
          ) : (
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="templates-import-kind">Kind</Label>
              <Input
                id="templates-import-kind"
                className="h-8 font-mono"
                placeholder="osdu:wks:master-data--Wellbore:1.3.0"
                value={typedKind}
                onChange={(event) => setTypedKind(event.target.value)}
                data-testid="templates-import-kind"
              />
              <p className="text-xs text-muted-foreground">
                Read from the schema&apos;s <span className="font-mono">x-osdu-schema-source</span>; give it here only for a schema that does not name its kind.
              </p>
            </div>
          )}
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="templates-import-file">Schema file</Label>
            <Input
              id="templates-import-file"
              ref={fileInput}
              type="file"
              accept=".json,application/json"
              className="h-8 text-[13px]"
              disabled={reading}
              onChange={(event) => choose(event.target.files?.[0])}
              data-testid="templates-import-file"
            />
            {reading && <p className="text-xs text-muted-foreground">Reading the file.</p>}
            {file !== null && !reading && (
              <p className="text-xs text-muted-foreground" data-testid="templates-import-file-name">
                Read <span className="font-mono">{file.name}</span>{text === file.text ? "." : ", and edited since."}
              </p>
            )}
          </div>
        </div>
        {outside.length > 0 && (
          <div className="flex flex-col gap-1.5" data-testid="templates-import-references">
            <div className="flex flex-wrap items-center gap-2">
              <Label htmlFor="templates-import-release">Shared schemas from release</Label>
              <Select value={release ?? ""} onValueChange={setPickedRelease} disabled={releases.data === undefined || working}>
                <SelectTrigger id="templates-import-release" size="sm" className="h-8 w-40" data-testid="templates-import-release">
                  <SelectValue placeholder={releases.isError ? "Unavailable" : "Loading releases"} />
                </SelectTrigger>
                <SelectContent>
                  {(releases.data?.releases ?? []).map((candidate, i) => (
                    <SelectItem key={candidate.name} value={candidate.name}>
                      <span className="font-mono">{candidate.name}</span>
                      {i === 0 && <span className="text-[11px] text-muted-foreground">newest</span>}
                      {candidate.local && <span className="text-[11px] text-success">on disk</span>}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <p className="text-xs text-muted-foreground" data-testid="templates-import-references-note">
              The schema refers to {outside.length} {outside.length === 1 ? "file" : "files"} outside itself, such
              as <span className="font-mono">{outside[0]}</span>. They are read from this release of the OSDU data definitions and
              bundled into the template, and the saved template names the release
              {chosenRelease !== undefined && !chosenRelease.local ? "; the release is downloaded into the local copy first." : "."}
            </p>
            {releases.isError && <ProblemView error={releases.error} testId="templates-import-releases-error" />}
          </div>
        )}
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="templates-import-json">Schema JSON</Label>
          <Textarea
            id="templates-import-json"
            className="field-sizing-fixed h-72 resize-y font-mono text-[12px]"
            spellCheck={false}
            placeholder="Paste the schema here, or choose a file above."
            value={text}
            onChange={(event) => setText(event.target.value)}
            data-testid="templates-import-json"
          />
        </div>
        {problem !== null && <p className="text-xs font-medium text-destructive" data-testid="templates-import-error">{problem}</p>}
        {preview.isError && <ProblemView error={preview.error} testId="templates-import-preview-error" />}
        {saveNow.isError && <ProblemView error={saveNow.error} testId="templates-import-save-now-error" />}
        <div className="flex flex-wrap items-center gap-2">
          {canAuthor && (
            <Button size="sm" onClick={runSave} disabled={working} data-testid="templates-import-save-now">
              {saveNow.isPending ? <Loader2 className="animate-spin" /> : <Save />}
              Save template
            </Button>
          )}
          <Button
            variant={canAuthor ? "outline" : "default"}
            size="sm"
            onClick={runPreview}
            disabled={working}
            data-testid="templates-import-preview"
          >
            {preview.isPending ? <Loader2 className="animate-spin" /> : <Eye />}
            Preview
          </Button>
          <Button variant="ghost" size="sm" onClick={clear} disabled={preview.isPending || saveNow.isPending} data-testid="templates-import-clear">
            <X />
            Clear
          </Button>
          {shown !== null && !sheetOpen && (
            <Button variant="link" size="sm" onClick={() => setSheetOpen(true)} data-testid="templates-import-reopen">
              Show the last preview
            </Button>
          )}
        </div>
      </Card>

      <TemplateSheet
        open={sheetOpen && shown !== null}
        onClose={() => setSheetOpen(false)}
        title={shown?.kind ?? "Schema"}
        source={shownOrigin !== null ? <span className="min-w-0 break-all">{originText(shownOrigin)}</span> : null}
        progress={null}
        problem={save.isError ? <ProblemView error={save.error} testId="templates-import-save-error" /> : null}
        detail={shown?.detail}
        previewSchema={shown?.schema}
        actions={canAuthor && shown !== null ? (
          <Button variant="outline" size="xs" onClick={() => save.mutate(shown)} disabled={save.isPending} data-testid="templates-import-save">
            {save.isPending ? <Loader2 className="animate-spin" /> : <Save />}
            Save template
          </Button>
        ) : undefined}
        busy={save.isPending}
        testId="templates-import-sheet"
      />
    </div>
  );
}
