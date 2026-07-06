import * as vscode from 'vscode';

const TOKEN_KEY = 'sqlflow.accessToken';

/**
 * Holds the control-plane access token in VS Code SecretStorage and drives the
 * OAuth device-authorization sign-in flow (the same grant the MCP server uses):
 * request a code, open the approval page in a browser, poll until approved.
 */
export class Session {
    private readonly onChange = new vscode.EventEmitter<void>();
    readonly changed = this.onChange.event;

    constructor(private readonly secrets: vscode.SecretStorage) {}

    async getToken(): Promise<string | undefined> {
        return this.secrets.get(TOKEN_KEY);
    }

    async setToken(token: string): Promise<void> {
        await this.secrets.store(TOKEN_KEY, token);
        this.onChange.fire();
    }

    async clear(): Promise<void> {
        await this.secrets.delete(TOKEN_KEY);
        this.onChange.fire();
    }

    async isSignedIn(): Promise<boolean> {
        return (await this.getToken()) !== undefined;
    }

    /** Sign in via device flow, or fall back to pasting a token. */
    async signIn(baseUrl: string): Promise<boolean> {
        const method = await vscode.window.showQuickPick(
            [
                { label: 'Sign in with a browser', detail: 'Device authorization: approve in the SQLFlow web page.', id: 'device' },
                { label: 'Paste an access token', detail: 'Use a token from the CLI or GUI.', id: 'token' },
            ],
            { placeHolder: 'How would you like to sign in?' }
        );
        if (!method) {
            return false;
        }
        return method.id === 'token' ? this.signInWithToken() : this.signInWithDevice(baseUrl);
    }

    private async signInWithToken(): Promise<boolean> {
        const token = await vscode.window.showInputBox({
            prompt: 'Paste a SQLFlow control-plane bearer token',
            password: true,
            ignoreFocusOut: true,
        });
        if (!token) {
            return false;
        }
        await this.setToken(token.trim());
        return true;
    }

    private async signInWithDevice(baseUrl: string): Promise<boolean> {
        let auth: DeviceAuth;
        try {
            const resp = await fetch(`${baseUrl}/api/v1/auth/device`, {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ clientId: 'sqlflow-vscode', scope: 'read operate' }),
            });
            if (!resp.ok) {
                throw new Error(`device authorization failed (${resp.status})`);
            }
            auth = (await resp.json()) as DeviceAuth;
        } catch (err) {
            void vscode.window.showErrorMessage(`SQLFlow sign-in could not start: ${errText(err)}`);
            return false;
        }

        const openUrl = auth.verificationUriComplete ?? auth.verificationUri;
        await vscode.env.openExternal(vscode.Uri.parse(openUrl));

        return vscode.window.withProgress(
            {
                location: vscode.ProgressLocation.Notification,
                title: `SQLFlow: approve sign-in — code ${auth.userCode}`,
                cancellable: true,
            },
            async (_progress, cancel) => {
                const deadline = Date.now() + auth.expiresIn * 1000;
                let interval = Math.max(1, auth.interval) * 1000;
                while (Date.now() < deadline && !cancel.isCancellationRequested) {
                    await delay(interval);
                    const outcome = await this.pollDevice(baseUrl, auth.deviceCode);
                    if (outcome === 'approved') {
                        void vscode.window.showInformationMessage('SQLFlow: signed in.');
                        return true;
                    }
                    if (outcome === 'slow_down') {
                        interval += 2000;
                    }
                    if (outcome === 'denied' || outcome === 'expired') {
                        void vscode.window.showWarningMessage(`SQLFlow sign-in ${outcome}.`);
                        return false;
                    }
                }
                return false;
            }
        );
    }

    private async pollDevice(baseUrl: string, deviceCode: string): Promise<PollOutcome> {
        try {
            const resp = await fetch(`${baseUrl}/api/v1/auth/device/token`, {
                method: 'POST',
                headers: { 'content-type': 'application/json' },
                body: JSON.stringify({ deviceCode }),
            });
            if (resp.ok) {
                const body = (await resp.json()) as { access_token: string };
                await this.setToken(body.access_token);
                return 'approved';
            }
            const err = (await resp.json().catch(() => ({}))) as { error?: string };
            switch (err.error) {
                case 'slow_down':
                    return 'slow_down';
                case 'access_denied':
                    return 'denied';
                case 'expired_token':
                    return 'expired';
                default:
                    return 'pending';
            }
        } catch {
            return 'pending';
        }
    }
}

interface DeviceAuth {
    deviceCode: string;
    userCode: string;
    verificationUri: string;
    verificationUriComplete?: string;
    expiresIn: number;
    interval: number;
}

type PollOutcome = 'approved' | 'pending' | 'slow_down' | 'denied' | 'expired';

function delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

export function errText(err: unknown): string {
    return err instanceof Error ? err.message : String(err);
}
