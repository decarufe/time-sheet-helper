import * as vscode from 'vscode';
import { pickRemoteUrl } from './logic';

// Minimal subset of the built-in `vscode.git` extension API (extensions/git/src/api/git.d.ts).
interface Remote {
  readonly name: string;
  readonly fetchUrl?: string;
  readonly pushUrl?: string;
}

interface Repository {
  readonly rootUri: vscode.Uri;
  readonly state: {
    readonly HEAD: { readonly name?: string } | undefined;
    readonly remotes: Remote[];
  };
}

interface GitAPI {
  readonly repositories: Repository[];
  getRepository(uri: vscode.Uri): Repository | null;
}

interface GitExtension {
  readonly enabled: boolean;
  getAPI(version: 1): GitAPI;
}

export interface GitInfo {
  remote: string | null;
  branch: string | null;
}

let api: GitAPI | undefined;

function getApi(): GitAPI | undefined {
  if (api) {
    return api;
  }
  try {
    const extension = vscode.extensions.getExtension<GitExtension>('vscode.git');
    // Only use it once VS Code has activated it; never force activation from here.
    if (extension?.isActive && extension.exports.enabled) {
      api = extension.exports.getAPI(1);
    }
  } catch {
    // Git disabled or API changed: git info is optional.
  }
  return api;
}

/** Git remote and branch of the repository holding `uri` (or of the first workspace folder). */
export function getGitInfo(uri: vscode.Uri | undefined): GitInfo {
  const git = getApi();
  if (!git) {
    return { remote: null, branch: null };
  }
  try {
    let repository = uri ? git.getRepository(uri) : null;
    if (!repository) {
      const folder = vscode.workspace.workspaceFolders?.[0]?.uri;
      repository = (folder && git.getRepository(folder)) || git.repositories[0] || null;
    }
    if (!repository) {
      return { remote: null, branch: null };
    }
    return {
      remote: pickRemoteUrl(repository.state.remotes),
      branch: repository.state.HEAD?.name ?? null,
    };
  } catch {
    return { remote: null, branch: null };
  }
}
