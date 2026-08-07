import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface CoverageSummary {
  linesCovered: number;
  linesCoverable: number;
  branchesCovered: number;
  branchesTotal: number;
  filesCount: number;
}

export interface RepoInfo {
  owner: string;
  name: string;
  fullName: string;
  isPrivate: boolean;
  defaultBranch?: string;
  latestCoverage?: CoverageSummary;
  latestCoverageSha?: string;
  canManage: boolean;
  badgeToken?: string;
}

export interface CommitInfo {
  sha: string;
  branch?: string;
  pullRequestNumber?: number;
  message?: string;
  authoredAt?: string;
  coverage?: CoverageSummary;
}

export interface SessionInfo {
  sessionId: string;
  jobName?: string;
  flags: string[];
  parseStatus: string;
  error?: string;
  filesCount: number;
}

export interface BuildInfo {
  runId: number;
  runAttempt: number;
  status: string;
  finalizeReason?: string;
  workflowName?: string;
  createdAtUtc: string;
  coverage?: CoverageSummary;
  sessions: SessionInfo[];
}

export interface CommitDetail extends CommitInfo {
  latestBuildId?: string;
  builds: BuildInfo[];
}

export interface TreeEntry {
  name: string;
  path: string;
  isFile: boolean;
  linesCovered: number;
  linesCoverable: number;
}

export interface TreeResponse {
  buildId: string;
  entries: TreeEntry[];
  unmatchedFiles: string[];
}

export function coveragePercent(summary?: CoverageSummary | null): number | null {
  if (!summary || summary.linesCoverable === 0) return null;
  return (summary.linesCovered / summary.linesCoverable) * 100;
}

@Injectable({ providedIn: 'root' })
export class BrowseService {
  private readonly http = inject(HttpClient);

  getAccountRepos(login: string): Promise<RepoInfo[]> {
    return firstValueFrom(this.http.get<RepoInfo[]>(`/api/browse/accounts/${encodeURIComponent(login)}/repos`));
  }

  getRepo(owner: string, name: string): Promise<RepoInfo> {
    return firstValueFrom(this.http.get<RepoInfo>(`/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}`));
  }

  getCommits(owner: string, name: string, branch?: string): Promise<CommitInfo[]> {
    let params = new HttpParams();
    if (branch) params = params.set('branch', branch);
    return firstValueFrom(this.http.get<CommitInfo[]>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits`, { params }));
  }

  getCommit(owner: string, name: string, sha: string): Promise<CommitDetail> {
    return firstValueFrom(this.http.get<CommitDetail>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}`));
  }

  getTree(owner: string, name: string, sha: string, path?: string): Promise<TreeResponse> {
    let params = new HttpParams();
    if (path) params = params.set('path', path);
    return firstValueFrom(this.http.get<TreeResponse>(
      `/api/browse/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/commits/${encodeURIComponent(sha)}/tree`, { params }));
  }

  rotateBadgeToken(owner: string, name: string): Promise<{ badgeToken: string }> {
    return firstValueFrom(this.http.post<{ badgeToken: string }>(
      `/api/repos/${encodeURIComponent(owner)}/${encodeURIComponent(name)}/settings/badge-token`, {}));
  }
}
