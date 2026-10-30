import * as path from 'path';
import * as vscode from 'vscode';
import { IndexedDocument } from '../../../../sqlflow/tools/sqlflow-vscode/src/documents/documentIndex';
import { ModuleContext, VscodeModule } from '../../../../sqlflow/tools/sqlflow-vscode/src/host';
import { InfoRow, InfoTreeProvider } from '../../../../sqlflow/tools/sqlflow-vscode/src/views/documentViews';
import { PanelContent, PanelSection } from '../../../../sqlflow/tools/sqlflow-vscode/src/views/panelContent';
import { JsonRow, rowsOf } from './jsonRows';
import { MappingEditing, WorkingCopyTemplates } from './mappingEditing';
import { declaredParameters } from './templates';

/** The flow kinds this product adds, each with the tool that says how it stands. */
const FLOW_READERS: Record<string, { label: string; tool: string; icon: string }> = {
    delivery: { label: 'Delivery', tool: 'delivery_flow', icon: 'cloud-upload' },
    assertion: { label: 'Assertions', tool: 'delivery_assertions', icon: 'beaker' },
    dimension: { label: 'Dimensions', tool: 'delivery_dimensions', icon: 'symbol-enum' },
    retrieval: { label: 'Retrievals', tool: 'delivery_retrievals', icon: 'cloud-download' },
    inventory: { label: 'Inventories', tool: 'delivery_inventories', icon: 'list-tree' },
    removal: { label: 'Removals', tool: 'delivery_removals', icon: 'trash' },
};

function infoRows(rows: JsonRow[]): InfoRow[] {
    return rows.map((row) => ({
        label: row.label,
        description: row.description,
        tooltip: row.description,
        children: row.children ? async () => infoRows(row.children ?? []) : undefined,
    }));
}

interface Issue {
    issue: string;
    pattern: string;
    records: number;
    held?: number;
    failed?: number;
    shape?: string;
}

interface CheckAnswer {
    loads?: boolean;
    issues?: { severity: string; message: string; variable?: string }[];
    requiredAndEmpty?: (string | { path?: string; variable?: string })[];
    links?: Record<string, string>;
}

/** The OSDU Delivery module: what the module's kinds and documents add to the extension. */
export const deliveryModule: VscodeModule = {
    activate(context: ModuleContext): void {
        const { estate, panels, documents, documentView, references, lenses, command, extension } = context;
        const mcp = estate.mcp;
        const templates = new WorkingCopyTemplates();
        const editing = new MappingEditing(templates, documents);
        const mappingFiles = async (): Promise<IndexedDocument[]> =>
            (await documents.all()).filter((document) => document.documentType === 'mapping');
        const selector: vscode.DocumentSelector = [{ language: 'sqlflow-flow' }];
        extension.subscriptions.push(
            templates,
            editing,
            vscode.languages.registerCompletionItemProvider(selector, editing, '.', ' '),
            vscode.languages.registerHoverProvider(selector, editing)
        );

        const delivery = new InfoTreeProvider(async () => [
            ['Partitions', 'database', 'delivery_partitions'],
            ['Mappings', 'symbol-namespace', 'delivery_mappings'],
            ['Templates', 'file-code', 'delivery_templates'],
            ['Caches', 'archive', 'delivery_caches'],
            ['Assertions', 'beaker', 'delivery_assertions'],
            ['Dimensions', 'symbol-enum', 'delivery_dimensions'],
            ['Inventories', 'list-tree', 'delivery_inventories'],
            ['Removals', 'trash', 'delivery_removals'],
            ['Configuration', 'settings-gear', 'delivery_config'],
        ].map(([label, icon, tool]) => ({
            label,
            icon,
            children: async () => infoRows(rowsOf(await mcp.call(tool))),
        })));
        extension.subscriptions.push(vscode.window.registerTreeDataProvider('osduDelivery', delivery), context.refreshed(() => delivery.refresh()));

        // What a flow of a kind this module adds says of itself.
        for (const [flowType, reader] of Object.entries(FLOW_READERS)) {
            documentView.register({
                label: reader.label,
                icon: reader.icon,
                expanded: flowType === 'delivery',
                applies: ({ document, pipeline }) => document.flowType === flowType && pipeline !== undefined,
                rows: async ({ pipeline }) => infoRows(rowsOf(await mcp.call(reader.tool, { pipelineId: pipeline?.id }))),
            });
        }
        documentView.register({
            label: 'Latest Submissions',
            icon: 'package',
            applies: ({ document, pipeline }) => document.flowType === 'delivery' && pipeline !== undefined,
            rows: async ({ pipeline }) => infoRows(rowsOf(await mcp.call('delivery_submissions', { pipelineId: pipeline?.id, max: 5 }))),
        });
        documentView.register({
            label: 'Cache',
            icon: 'archive',
            applies: ({ document, pipeline }) => document.flowType === 'cache' && pipeline !== undefined,
            rows: async ({ pipeline }) => infoRows(rowsOf(await mcp.call('delivery_caches', { repoId: pipeline?.repoId }))),
        });
        documentView.register({
            label: 'Audit Trail',
            icon: 'history',
            applies: ({ document, pipeline }) => pipeline !== undefined && (document.flowType === 'cache' || document.flowType === 'download' || document.flowType! in FLOW_READERS),
            rows: async ({ pipeline }) => infoRows(rowsOf(await mcp.call('delivery_activities', { pipelineId: pipeline?.id, pageSize: 10 }))),
        });
        documentView.register({
            label: 'Issues',
            icon: 'warning',
            expanded: true,
            applies: ({ document, pipeline }) => document.flowType === 'delivery' && pipeline !== undefined,
            rows: async ({ pipeline }) => {
                const answer = await mcp.call<{ issues?: Issue[] }>('delivery_flow_issues', { pipelineId: pipeline?.id });
                const issues = answer.issues ?? [];
                return issues.length === 0
                    ? [{ label: 'Nothing keeps its records blocked.', icon: 'pass' }]
                    : issues.map((issue) => ({
                        label: `${issue.records} records`,
                        description: issue.pattern,
                        tooltip: issue.pattern,
                        icon: 'warning',
                    }));
            },
        });
        documentView.register({
            label: 'Template',
            icon: 'file-code',
            expanded: true,
            applies: ({ document }) => document.documentType === 'mapping',
            rows: async ({ document, text }) => {
                const kind = /^template\s*:\s*\r?\n\s+kind\s*:\s*["']?([^\s"'#]+)/m.exec(text)?.[1];
                if (!kind) {
                    return [{ label: 'This mapping names no template yet.' }];
                }
                const file = await templates.forDocument(kind, document.uri);
                return [
                    { label: 'Kind', description: kind, icon: 'tag' },
                    file
                        ? { label: 'File', description: vscode.workspace.asRelativePath(file.uri), icon: 'go-to-file', command: { command: 'vscode.open', title: 'Open', arguments: [file.uri] } }
                        : { label: 'File', description: 'no template folder on this path holds it', icon: 'warning' },
                ];
            },
        });
        documentView.register({
            label: 'Flows Rendering It',
            icon: 'symbol-event',
            applies: ({ document }) => document.documentType === 'mapping',
            rows: async ({ text }) => {
                const name = /^name\s*:\s*(\S+)/m.exec(text)?.[1];
                const version = /^version\s*:\s*(\S+)/m.exec(text)?.[1];
                const flows = (await documents.all()).filter((document) => document.flowType === 'delivery');
                const reference = `${name}@${version}`;
                const hits = await Promise.all(flows.map(async (flow) => {
                    const body = Buffer.from(await vscode.workspace.fs.readFile(flow.uri)).toString('utf8');
                    return body.includes(reference) ? flow : undefined;
                }));
                const rendering = hits.filter((flow): flow is IndexedDocument => flow !== undefined);
                return rendering.length === 0
                    ? [{ label: 'No flow of this repository pins it.' }]
                    : rendering.map((flow) => ({
                        label: flow.name ?? flow.relativePath,
                        icon: 'symbol-event',
                        command: { command: 'vscode.open', title: 'Open', arguments: [flow.uri] },
                    }));
            },
        });

        // What a delivery flow's and a mapping's keys refer to.
        references.register({
            answers: (keyPath) => keyPath === 'render.mapping' || keyPath === 'interfaces.mapping' || keyPath.endsWith('.render.mapping'),
            values: async () => (await mappingFiles()).map((file) => ({
                value: path.basename(file.uri.fsPath).replace(/\.ya?ml$/, ''),
                detail: file.relativePath,
            })),
            target: async (value) => (await mappingFiles()).find((file) => path.basename(file.uri.fsPath).replace(/\.ya?ml$/, '') === value)?.uri,
        });
        references.register({
            answers: (keyPath) => keyPath === 'template.kind' || /^searches\..+\.schema\.kind$/.test(keyPath),
            values: async () => (await templates.all()).map((file) => ({ value: file.kind, detail: vscode.workspace.asRelativePath(file.uri) })),
            target: async (value, document) => (await templates.forDocument(value, document.uri))?.uri,
        });
        references.register({
            answers: (keyPath, document) => document?.kind === 'flow' && (keyPath === 'partitions.name' || keyPath === 'partition'),
            values: async () => {
                const answer = await mcp.call<{ partitions?: { name: string; description?: string }[] } | { name: string; description?: string }[]>('delivery_partitions');
                const partitions = Array.isArray(answer) ? answer : answer.partitions ?? [];
                return partitions.map((partition) => ({ value: partition.name, detail: partition.description }));
            },
        });

        const checkMapping = async (uri: vscode.Uri): Promise<void> => {
            const document = await vscode.workspace.openTextDocument(uri);
            const pipeline = (await estate.repos())[0];
            const answer = await mcp.call<CheckAnswer>('delivery_check_mapping', {
                yaml: document.getText(),
                path: vscode.workspace.asRelativePath(uri, false).replace(/\\/g, '/'),
                repoId: pipeline?.id,
            });
            panels.show(`mapping:${uri.toString()}`, checkContent(path.basename(uri.fsPath), answer));
        };

        command('osduDelivery.checkMapping', async (arg: unknown) => {
            const uri = arg instanceof vscode.Uri ? arg : vscode.window.activeTextEditor?.document.uri;
            if (uri) {
                await checkMapping(uri);
            }
        });

        command('osduDelivery.scaffoldMapping', async () => {
            const repos = await estate.repos();
            const listed = await mcp.call<{ repoId: string; repository?: string; path: string; kind?: string; version?: string }[] | { items: { repoId: string; path: string; kind?: string }[] }>('delivery_templates');
            const files = Array.isArray(listed) ? listed : listed.items;
            const template = await vscode.window.showQuickPick(
                files.filter((file) => file.kind).map((file) => ({ label: file.kind ?? file.path, description: file.path, file })),
                { placeHolder: 'Which template should the mapping fill?', matchOnDescription: true }
            );
            if (!template) {
                return;
            }
            const name = await vscode.window.showInputBox({ prompt: 'The mapping\'s name', value: /--([\w]+):/.exec(template.label)?.[1], ignoreFocusOut: true });
            const system = name && (await vscode.window.showInputBox({ prompt: 'The source system its records come from', ignoreFocusOut: true }));
            if (!name || !system) {
                return;
            }
            const answer = await mcp.call<{ yaml: string }>('delivery_scaffold_mapping', {
                repoId: template.file.repoId ?? repos[0]?.id,
                templatePath: template.file.path,
                name,
                mappingVersion: '1.0.0',
                system,
            });
            const document = await vscode.workspace.openTextDocument({ language: 'sqlflow-flow', content: answer.yaml });
            await vscode.window.showTextDocument(document);
        });

        command('osduDelivery.compareSchemaVersions', async () => {
            const fromRelease = await vscode.window.showInputBox({ prompt: 'Release of the version in use (a tag delivery_osdu_schemas lists)', ignoreFocusOut: true });
            const fromKind = fromRelease && (await vscode.window.showInputBox({ prompt: 'Kind in use, for example osdu:wks:work-product-component--WellLog:1.4.0', ignoreFocusOut: true }));
            const toRelease = fromKind && (await vscode.window.showInputBox({ prompt: 'Release to move to', value: fromRelease, ignoreFocusOut: true }));
            const toKind = toRelease && (await vscode.window.showInputBox({ prompt: 'Kind to move to', value: fromKind, ignoreFocusOut: true }));
            if (!toKind) {
                return;
            }
            const answer = await mcp.call<unknown>('delivery_osdu_schema_compare', { fromRelease, fromKind, toRelease, toKind });
            panels.show(`compare:${fromKind}:${toKind}`, {
                title: `${fromKind} to ${toKind}`,
                sections: [{ heading: 'What a mapping has to change', code: JSON.stringify(answer, null, 2) }],
            });
        });

        command('osduDelivery.findRecord', async () => {
            const search = await vscode.window.showInputBox({ prompt: 'An OSDU id, a source key, or the start of a label', ignoreFocusOut: true });
            if (!search) {
                return;
            }
            const answer = await mcp.call<{ items?: { flowId: string; deliveryKey: string; label?: string; targetId?: string; status?: string }[] }>('delivery_find_records', { search });
            const hit = await vscode.window.showQuickPick(
                (answer.items ?? []).map((item) => ({ label: item.label ?? item.targetId ?? item.deliveryKey, description: item.status, detail: item.targetId, item })),
                { placeHolder: 'Which record?', matchOnDetail: true }
            );
            if (hit) {
                const record = await mcp.call<unknown>('delivery_record', { flowId: hit.item.flowId, deliveryKey: hit.item.deliveryKey });
                panels.show(`record:${hit.item.deliveryKey}`, { title: hit.label, sections: [{ heading: 'What the ledger holds', code: JSON.stringify(record, null, 2) }] });
            }
        });

        command('osduDelivery.probeTarget', async () => {
            const document = vscode.window.activeTextEditor ? await documents.at(vscode.window.activeTextEditor.document.uri) : undefined;
            const pipeline = document?.name ? await estate.pipelineNamed(document.name, document.relativePath) : undefined;
            if (!pipeline) {
                void vscode.window.showInformationMessage('Open a delivery flow the estate holds.');
                return;
            }
            const answer = await mcp.call<unknown>('delivery_probe_target', { pipelineId: pipeline.id });
            panels.show(`probe:${pipeline.id}`, { title: `${pipeline.name}: its OSDU`, sections: [{ heading: 'How it answered', code: JSON.stringify(answer, null, 2) }] });
        });

        lenses.register(async (document) => (document.documentType === 'mapping'
            ? [{ title: '$(checklist) Check Mapping', command: 'osduDelivery.checkMapping', arguments: [document.uri] }]
            : []));

        context.addRunParameters(async (pipeline, document) => {
            if (document) {
                const text = (await vscode.workspace.openTextDocument(document.uri)).getText();
                return declaredParameters(text.split(/\r?\n/));
            }
            const detail = await estate.pipeline(pipeline.id);
            return declaredParameters(detail.yaml.split(/\r?\n/));
        });

        context.addRunSections(async (run): Promise<PanelSection[]> => {
            if (run.flowKind !== 'delivery') {
                return [];
            }
            const answer = await mcp.call<unknown>('delivery_run', { id: run.runId }).catch(() => undefined);
            return answer === undefined ? [] : [{ heading: 'What it delivered', code: JSON.stringify(answer, null, 2) }];
        });

    },
};

/** A mapping check as a person reads it: whether it loads, what is wrong, what OSDU would refuse. */
function checkContent(file: string, answer: CheckAnswer): PanelContent {
    const required = (answer.requiredAndEmpty ?? []).map((entry) => (typeof entry === 'string' ? entry : entry.path ?? entry.variable ?? ''));
    return {
        title: `${file}: check`,
        subtitle: answer.loads === false ? 'It does not load.' : 'Checked against its template by the control plane.',
        actions: answer.links?.page ? [{ text: 'Open in the mapping builder', url: answer.links.page }] : undefined,
        sections: [
            {
                heading: 'Required and empty',
                columns: ['Property'],
                rows: required.map((property) => [property]),
                empty: 'Everything the template requires is filled.',
            },
            {
                heading: 'Issues',
                columns: ['Severity', 'Variable', 'Message'],
                rows: (answer.issues ?? []).map((issue) => [issue.severity, issue.variable, issue.message]),
                empty: 'No issues.',
            },
        ],
    };
}
