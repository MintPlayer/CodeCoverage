import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BrowseService, CommitInfo, RepoInfo, coveragePercent } from '../../services/browse.service';
import { CoverageBarComponent } from '../../components/coverage-bar/coverage-bar.component';
import { RepoTrendPanelComponent } from '../../components/repo-trend-panel/repo-trend-panel.component';
import { RepoSetupPanelComponent } from '../../components/repo-setup-panel/repo-setup-panel.component';

@Component({
  selector: 'app-repo',
  imports: [CommonModule, DatePipe, RouterModule, FormsModule, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent, BsBadgeComponent, BsSpinnerComponent, BsSelectComponent, BsSelectOption, CoverageBarComponent, RepoTrendPanelComponent, RepoSetupPanelComponent],
  templateUrl: './repo.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class RepoComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);

  readonly owner = signal('');
  readonly name = signal('');
  readonly repo = signal<RepoInfo | null>(null);
  readonly commits = signal<CommitInfo[] | null>(null);
  readonly branches = signal<string[]>([]);
  /** '' = all branches. */
  readonly selectedBranch = signal('');

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      const owner = params.get('owner') ?? '';
      const name = params.get('repo') ?? '';
      this.owner.set(owner);
      this.name.set(name);
      this.repo.set(null);
      this.commits.set(null);
      this.branches.set([]);
      this.selectedBranch.set('');
      const [repo, commits, branches] = await Promise.all([
        this.browse.getRepo(owner, name),
        this.browse.getCommits(owner, name),
        this.browse.getBranches(owner, name).catch(() => [] as string[]),
      ]);
      this.repo.set(repo);
      this.commits.set(commits);
      this.branches.set(branches);
    });
  }

  async selectBranch(branch: string): Promise<void> {
    this.selectedBranch.set(branch);
    this.commits.set(null);
    this.commits.set(await this.browse.getCommits(this.owner(), this.name(), branch || undefined));
  }

  /** Delta vs the previous (older) commit in the list, in percent points. */
  delta(index: number): number | null {
    const list = this.commits();
    if (!list || index + 1 >= list.length) return null;
    const current = coveragePercent(list[index].coverage);
    const previous = coveragePercent(list[index + 1].coverage);
    if (current === null || previous === null) return null;
    return current - previous;
  }

  badgeMarkdown(): string {
    const r = this.repo();
    if (!r) return '';
    const origin = r.baseUrl || location.origin;
    const base = `${origin}/badge/${r.owner}/${r.name}.svg`;
    const url = r.isPrivate && r.badgeToken ? `${base}?token=${r.badgeToken}` : base;
    return `[![Coverage](${url})](${origin}/r/${r.owner}/${r.name})`;
  }

  async copyBadge(): Promise<void> {
    await navigator.clipboard.writeText(this.badgeMarkdown());
  }

  async rotateBadgeToken(): Promise<void> {
    const r = this.repo();
    if (!r) return;
    const result = await this.browse.rotateBadgeToken(r.owner, r.name);
    this.repo.set({ ...r, badgeToken: result.badgeToken });
  }
}
