import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { getGitInfo } from './git';
import { AiSignals, detectAiActivity, Heartbeat, HeartbeatQueue } from './logic';

const HEARTBEAT_INTERVAL_MS = 30_000;
const CHANGE_DEBOUNCE_MS = 1_000;
const REQUEST_TIMEOUT_MS = 3_000;
const TOKEN_HEADER = 'X-TimeSheet-Token';

class HeartbeatSender implements vscode.Disposable {
  private readonly queue = new HeartbeatQueue();
  private readonly disposables: vscode.Disposable[] = [];
  private timer: NodeJS.Timeout | undefined;
  private debounce: NodeJS.Timeout | undefined;
  private sending: Promise<void> = Promise.resolve();
  private editsSinceLast = 0;
  private lastFocused = vscode.window.state.focused;
  private cachedFileToken: string | undefined;

  start(): void {
    const onChange = () => this.scheduleImmediate();
    this.disposables.push(
      vscode.window.onDidChangeWindowState((state) => this.onWindowState(state)),
      vscode.window.onDidChangeActiveTextEditor(onChange),
      vscode.window.onDidChangeActiveTerminal(onChange),
      vscode.window.tabGroups.onDidChangeTabGroups(onChange),
      vscode.window.tabGroups.onDidChangeTabs(onChange),
      vscode.workspace.onDidChangeWorkspaceFolders(onChange),
      vscode.debug.onDidStartDebugSession(onChange),
      vscode.debug.onDidTerminateDebugSession(onChange),
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (e.contentChanges.length > 0 && (e.document.uri.scheme === 'file' || e.document.uri.scheme === 'untitled')) {
          this.editsSinceLast++;
        }
      }),
      vscode.workspace.onDidChangeConfiguration((e) => {
        if (e.affectsConfiguration('timesheetHelper')) {
          this.cachedFileToken = undefined;
          this.scheduleImmediate();
        }
      }),
    );
    this.timer = setInterval(() => {
      if (vscode.window.state.focused) {
        this.enqueueSend(true);
      }
    }, HEARTBEAT_INTERVAL_MS);
    if (vscode.window.state.focused) {
      this.enqueueSend(true);
    }
  }

  dispose(): void {
    clearInterval(this.timer);
    clearTimeout(this.debounce);
    this.disposables.forEach((d) => d.dispose());
  }

  private onWindowState(state: vscode.WindowState): void {
    if (state.focused === this.lastFocused) {
      return;
    }
    this.lastFocused = state.focused;
    clearTimeout(this.debounce);
    // Focus is reported right away (no debounce) so the app knows exactly when the window was left.
    this.enqueueSend(state.focused);
  }

  private scheduleImmediate(): void {
    if (!vscode.window.state.focused) {
      return;
    }
    clearTimeout(this.debounce);
    this.debounce = setTimeout(() => this.enqueueSend(true), CHANGE_DEBOUNCE_MS);
  }

  private enqueueSend(focused: boolean): void {
    if (!isEnabled()) {
      return;
    }
    const heartbeat = this.buildHeartbeat(focused);
    // Serialize sends so heartbeats reach the app in chronological order.
    this.sending = this.sending.then(() => this.send(heartbeat)).catch(() => undefined);
  }

  private buildHeartbeat(focused: boolean): Heartbeat {
    const editor = vscode.window.activeTextEditor;
    const document = editor?.document;
    const activeFile = document && document.uri.scheme === 'file' ? document.uri.fsPath : null;
    const git = getGitInfo(document?.uri);
    const edits = this.editsSinceLast;
    this.editsSinceLast = 0;

    return {
      sessionId: vscode.env.sessionId,
      timestampUtc: new Date().toISOString(),
      windowFocused: focused,
      workspaceName: vscode.workspace.name ?? null,
      workspaceFolders: (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath),
      activeFile,
      languageId: document?.languageId ?? null,
      gitRemote: git.remote,
      gitBranch: git.branch,
      editsSinceLast: edits,
      debugging: vscode.debug.activeDebugSession !== undefined,
      aiActivity: detectAiActivity(collectAiSignals()),
    };
  }

  private async send(heartbeat: Heartbeat): Promise<void> {
    const pending = [...this.queue.drain(), heartbeat];
    for (let i = 0; i < pending.length; i++) {
      const result = await this.post(pending[i]);
      if (result === 'offline') {
        // App not running: keep the remainder for later, silently.
        this.queue.requeue(pending.slice(i));
        return;
      }
      // 'ok' or 'rejected' (bad token / payload): move on, a rejected heartbeat would never succeed.
    }
  }

  private async post(heartbeat: Heartbeat): Promise<'ok' | 'rejected' | 'offline'> {
    const token = this.getToken();
    if (!token) {
      // No token yet means the app never started on this machine: treat as offline.
      return 'offline';
    }
    try {
      const response = await fetch(getEndpoint(), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json', [TOKEN_HEADER]: token },
        body: JSON.stringify(heartbeat),
        signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS),
      });
      if (response.status === 401) {
        // The app may have regenerated its token: re-read the file next time.
        this.cachedFileToken = undefined;
        return 'rejected';
      }
      return response.ok ? 'ok' : 'rejected';
    } catch {
      return 'offline';
    }
  }

  private getToken(): string | undefined {
    const configured = vscode.workspace.getConfiguration('timesheetHelper').get<string>('token')?.trim();
    if (configured) {
      return configured;
    }
    if (this.cachedFileToken === undefined) {
      this.cachedFileToken = readTokenFile();
    }
    return this.cachedFileToken || undefined;
  }
}

function isEnabled(): boolean {
  return vscode.workspace.getConfiguration('timesheetHelper').get<boolean>('enabled', true);
}

function getEndpoint(): string {
  return (
    vscode.workspace.getConfiguration('timesheetHelper').get<string>('endpoint')?.trim() ||
    'http://localhost:5178/ingest/vscode'
  );
}

function readTokenFile(): string | undefined {
  const localAppData = process.env.LOCALAPPDATA;
  if (!localAppData) {
    return undefined;
  }
  try {
    return fs.readFileSync(path.join(localAppData, 'TimeSheetHelper', 'ingest-token'), 'utf8').trim() || undefined;
  } catch {
    return undefined;
  }
}

function collectAiSignals(): AiSignals {
  const signals: AiSignals = { activeTerminalName: vscode.window.activeTerminal?.name ?? null };
  const tab = vscode.window.tabGroups.activeTabGroup?.activeTab;
  if (!tab) {
    return signals;
  }
  signals.activeTabLabel = tab.label;
  const input = tab.input;
  if (input instanceof vscode.TabInputWebview || input instanceof vscode.TabInputCustom) {
    signals.activeTabViewType = input.viewType;
  } else if (
    input instanceof vscode.TabInputText ||
    input instanceof vscode.TabInputTextDiff ||
    input instanceof vscode.TabInputNotebook ||
    input instanceof vscode.TabInputNotebookDiff
  ) {
    // File tab: leave the view type undefined so the label is not used.
  } else {
    // Unknown tab kinds (e.g. chat editors on newer VS Code): judge by label.
    signals.activeTabViewType = null;
  }
  return signals;
}

export function activate(context: vscode.ExtensionContext): void {
  const sender = new HeartbeatSender();
  context.subscriptions.push(sender);
  sender.start();
}

export function deactivate(): void {
  // Disposed through context.subscriptions.
}
