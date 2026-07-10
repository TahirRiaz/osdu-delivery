import * as vscode from 'vscode';
import { AuthError, ControlPlaneClient, Pipeline, Repo, Run, Schedule } from '../api/controlPlaneClient';

/** Unwrap either a PagedResult<T> or a bare T[] into T[]. */
function items<T>(result: { items: T[] } | T[]): T[] {
    return Array.isArray(result) ? result : result.items;
}

/** A leaf node shown when the catalog cannot be loaded (e.g. not signed in). */
function messageNode(label: string, command?: vscode.Command): vscode.TreeItem {
    const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
    item.command = command;
    item.iconPath = new vscode.ThemeIcon('info');
    return item;
}

export class RepoNode extends vscode.TreeItem {
    constructor(public readonly repo: Repo) {
        super(repo.name ?? repo.id, vscode.TreeItemCollapsibleState.Collapsed);
        this.contextValue = 'repo';
        this.iconPath = new vscode.ThemeIcon('repo');
        this.id = `repo:${repo.id}`;
    }
}

export class PipelineNode extends vscode.TreeItem {
    constructor(public readonly pipeline: Pipeline) {
        super(pipeline.name ?? pipeline.id, vscode.TreeItemCollapsibleState.None);
        this.contextValue = 'pipeline';
        this.description = [pipeline.kind, pipeline.batch].filter(Boolean).join(' · ');
        this.iconPath = new vscode.ThemeIcon(pipeline.active === false ? 'circle-slash' : 'symbol-event');
        this.tooltip = `${pipeline.name}\nkind: ${pipeline.kind ?? 'file'}\nactive: ${pipeline.active ?? true}`;
        this.command = { command: 'sqlflow.openPipeline', title: 'Open', arguments: [this] };
    }
}

export class CatalogTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly client: ControlPlaneClient) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(element?: vscode.TreeItem): Promise<vscode.TreeItem[]> {
        try {
            if (!element) {
                const repos = items(await this.client.listRepos());
                return repos.length ? repos.map((r) => new RepoNode(r)) : [messageNode('No repositories synced yet.')];
            }
            if (element instanceof RepoNode) {
                const pipelines = items(await this.client.listPipelines({ repoId: element.repo.id }));
                // Stamp the owning repo so a run can be triggered straight from the node.
                pipelines.forEach((p) => (p.repoId = p.repoId ?? element.repo.id));
                return pipelines.length ? pipelines.map((p) => new PipelineNode(p)) : [messageNode('No flows in this repository.')];
            }
            return [];
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

export class RunNode extends vscode.TreeItem {
    constructor(public readonly run: Run) {
        super(run.flowName ?? run.runId, vscode.TreeItemCollapsibleState.None);
        const active = run.status === 'queued' || run.status === 'running';
        this.contextValue = active ? 'run-active' : 'run';
        this.description = run.status;
        this.iconPath = new vscode.ThemeIcon(statusIcon(run.status));
        this.tooltip = `${run.flowName ?? ''}\nstatus: ${run.status}\nstarted: ${run.startedUtc ?? '-'}`;
        this.command = { command: 'sqlflow.openRun', title: 'Open', arguments: [this] };
    }
}

export class RunsTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly client: ControlPlaneClient) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(): Promise<vscode.TreeItem[]> {
        try {
            const limit = vscode.workspace.getConfiguration('sqlflow').get<number>('runResultLimit', 50);
            const runs = items(await this.client.listRuns({ pageSize: limit }));
            return runs.length ? runs.map((r) => new RunNode(r)) : [messageNode('No runs yet.')];
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

export class SchedulesTreeProvider implements vscode.TreeDataProvider<vscode.TreeItem> {
    private readonly emitter = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.emitter.event;

    constructor(private readonly client: ControlPlaneClient) {}

    refresh(): void {
        this.emitter.fire();
    }

    getTreeItem(element: vscode.TreeItem): vscode.TreeItem {
        return element;
    }

    async getChildren(): Promise<vscode.TreeItem[]> {
        try {
            const schedules = items(await this.client.listSchedules());
            return schedules.length
                ? schedules.map((s) => new ScheduleNode(s))
                : [messageNode('No schedules configured.')];
        } catch (err) {
            return [errorNode(err)];
        }
    }
}

export class ScheduleNode extends vscode.TreeItem {
    constructor(public readonly schedule: Schedule) {
        super(schedule.name ?? schedule.id, vscode.TreeItemCollapsibleState.None);
        this.description = [schedule.cron, schedule.paused ? 'paused' : undefined].filter(Boolean).join(' · ');
        this.iconPath = new vscode.ThemeIcon(schedule.paused ? 'debug-pause' : 'watch');
        this.contextValue = 'schedule';
    }
}

function errorNode(err: unknown): vscode.TreeItem {
    if (err instanceof AuthError) {
        return messageNode('Sign in to view…', { command: 'sqlflow.signIn', title: 'Sign In' });
    }
    const item = messageNode(err instanceof Error ? err.message : String(err));
    item.iconPath = new vscode.ThemeIcon('error');
    return item;
}

function statusIcon(status: string): string {
    switch (status) {
        case 'succeeded':
            return 'pass';
        case 'failed':
            return 'error';
        case 'running':
            return 'sync~spin';
        case 'queued':
            return 'clock';
        case 'cancelled':
            return 'circle-slash';
        default:
            return 'circle-outline';
    }
}
