// Pure logic (no `vscode` import) so it can be unit-tested with vitest.

export interface Heartbeat {
  sessionId: string;
  timestampUtc: string;
  windowFocused: boolean;
  workspaceName: string | null;
  workspaceFolders: string[];
  activeFile: string | null;
  languageId: string | null;
  gitRemote: string | null;
  gitBranch: string | null;
  editsSinceLast: number;
  debugging: boolean;
  aiActivity: boolean;
}

/**
 * Inputs for the AI-activity heuristic. VS Code exposes no API telling whether an AI assistant is working,
 * so we look at what the user is looking at:
 *  - the active terminal's name contains "claude" (Claude Code CLI run in the integrated terminal), or
 *  - the active editor tab is a chat view: a webview / custom tab whose view type or label mentions
 *    "claude", "copilot" or "chat" (Claude Code panel, Copilot Chat in the editor area, "Chat" editors).
 * Chat views docked in a sidebar are not tabs and are not detected.
 */
export interface AiSignals {
  activeTerminalName?: string | null;
  activeTabLabel?: string | null;
  /** View type of a webview / custom editor tab, or "chat" for a built-in chat editor tab. */
  activeTabViewType?: string | null;
}

const terminalPattern = /claude/i;
const chatTabPattern = /claude|copilot|\bchat\b/i;

export function detectAiActivity(signals: AiSignals): boolean {
  if (signals.activeTerminalName && terminalPattern.test(signals.activeTerminalName)) {
    return true;
  }
  if (signals.activeTabViewType && chatTabPattern.test(signals.activeTabViewType)) {
    return true;
  }
  // A label alone is only trusted for non-file tabs (a file named "chat.ts" is not a chat view).
  if (signals.activeTabViewType !== undefined && signals.activeTabLabel && chatTabPattern.test(signals.activeTabLabel)) {
    return true;
  }
  return false;
}

/**
 * Offline buffer: keeps the most recent heartbeats while the app is unreachable, bounded by age and count,
 * so a restart of the app recovers the last few minutes without unbounded growth.
 */
export class HeartbeatQueue {
  private items: Heartbeat[] = [];

  constructor(
    private readonly maxAgeMs: number = 5 * 60_000,
    private readonly maxItems: number = 20,
  ) {}

  get length(): number {
    return this.items.length;
  }

  push(heartbeat: Heartbeat, nowMs: number = Date.now()): void {
    this.items.push(heartbeat);
    this.prune(nowMs);
  }

  /** Puts back heartbeats that could not be sent, ahead of newer ones. */
  requeue(heartbeats: Heartbeat[], nowMs: number = Date.now()): void {
    this.items = [...heartbeats, ...this.items];
    this.prune(nowMs);
  }

  /** Removes and returns all queued heartbeats, oldest first. */
  drain(nowMs: number = Date.now()): Heartbeat[] {
    this.prune(nowMs);
    const drained = this.items;
    this.items = [];
    return drained;
  }

  private prune(nowMs: number): void {
    const minTime = nowMs - this.maxAgeMs;
    this.items = this.items.filter((h) => Date.parse(h.timestampUtc) >= minTime);
    if (this.items.length > this.maxItems) {
      this.items = this.items.slice(this.items.length - this.maxItems);
    }
  }
}

/** Picks the "origin" remote, else the first one with a URL. */
export function pickRemoteUrl(remotes: ReadonlyArray<{ name: string; fetchUrl?: string; pushUrl?: string }>): string | null {
  const origin = remotes.find((r) => r.name === 'origin');
  const chosen = origin ?? remotes.find((r) => r.fetchUrl || r.pushUrl);
  return chosen?.fetchUrl ?? chosen?.pushUrl ?? null;
}
