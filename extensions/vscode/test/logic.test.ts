import { describe, expect, it } from 'vitest';
import { detectAiActivity, Heartbeat, HeartbeatQueue, pickRemoteUrl } from '../src/logic';

const T0 = Date.parse('2026-10-05T13:00:00.000Z');

function heartbeat(offsetMs: number): Heartbeat {
  return {
    sessionId: 's1',
    timestampUtc: new Date(T0 + offsetMs).toISOString(),
    windowFocused: true,
    workspaceName: 'billing',
    workspaceFolders: ['C:\\src\\billing'],
    activeFile: null,
    languageId: null,
    gitRemote: null,
    gitBranch: null,
    editsSinceLast: 0,
    debugging: false,
    aiActivity: false,
  };
}

describe('detectAiActivity', () => {
  it('detects a Claude terminal', () => {
    expect(detectAiActivity({ activeTerminalName: 'claude' })).toBe(true);
    expect(detectAiActivity({ activeTerminalName: 'Claude Code' })).toBe(true);
    expect(detectAiActivity({ activeTerminalName: 'pwsh' })).toBe(false);
  });

  it('detects chat webview tabs', () => {
    expect(detectAiActivity({ activeTabViewType: 'mainThreadWebview-claudeVSCodePanel', activeTabLabel: 'Claude Code' })).toBe(true);
    expect(detectAiActivity({ activeTabViewType: null, activeTabLabel: 'Copilot Chat' })).toBe(true);
    expect(detectAiActivity({ activeTabViewType: 'markdown.preview', activeTabLabel: 'Preview README.md' })).toBe(false);
  });

  it('ignores file tabs whose name looks like chat', () => {
    expect(detectAiActivity({ activeTabLabel: 'chat.ts' })).toBe(false);
  });
});

describe('HeartbeatQueue', () => {
  it('drops heartbeats older than the max age', () => {
    const queue = new HeartbeatQueue(60_000, 100);
    queue.push(heartbeat(0), T0);
    queue.push(heartbeat(50_000), T0 + 50_000);
    expect(queue.drain(T0 + 90_000).map((h) => h.timestampUtc)).toEqual([heartbeat(50_000).timestampUtc]);
    expect(queue.length).toBe(0);
  });

  it('keeps only the most recent items and requeues in order', () => {
    const queue = new HeartbeatQueue(600_000, 2);
    queue.push(heartbeat(0), T0);
    queue.push(heartbeat(1_000), T0);
    queue.push(heartbeat(2_000), T0);
    const drained = queue.drain(T0);
    expect(drained.map((h) => h.timestampUtc)).toEqual([heartbeat(1_000).timestampUtc, heartbeat(2_000).timestampUtc]);

    queue.push(heartbeat(3_000), T0);
    queue.requeue([heartbeat(2_000)], T0);
    expect(queue.drain(T0).map((h) => h.timestampUtc)).toEqual([heartbeat(2_000).timestampUtc, heartbeat(3_000).timestampUtc]);
  });
});

describe('pickRemoteUrl', () => {
  it('prefers origin', () => {
    expect(pickRemoteUrl([{ name: 'upstream', fetchUrl: 'u' }, { name: 'origin', fetchUrl: 'o' }])).toBe('o');
    expect(pickRemoteUrl([{ name: 'upstream', fetchUrl: 'u' }])).toBe('u');
    expect(pickRemoteUrl([])).toBeNull();
  });
});
