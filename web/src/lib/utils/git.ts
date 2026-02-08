/**
 * Git URL utilities for building clickable links from repository URLs.
 * Currently GitHub-only. Extend for GitLab/Bitbucket if needed.
 */

export function buildCommitUrl(repoUrl: string, sha: string): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');
  return `${normalized}/commit/${sha}`;
}

export function buildBranchUrl(repoUrl: string, branch: string): string {
  const normalized = repoUrl.replace(/\.git$/, '').replace(/\/$/, '');
  return `${normalized}/tree/${encodeURIComponent(branch)}`;
}

export function extractPrLabel(url: string): string {
  const match = url.match(/\/pull\/(\d+)/);
  if (match) return `PR #${match[1]}`;
  return 'PR';
}
