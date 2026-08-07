import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BrowseService, CommitInfo, RepoInfo, coveragePercent } from '../../services/browse.service';
import { CoverageBarComponent } from '../../components/coverage-bar/coverage-bar.component';

@Component({
  selector: 'app-repo',
  imports: [CommonModule, DatePipe, RouterModule, BsCardComponent, BsCardHeaderComponent, BsTableComponent, BsBadgeComponent, BsSpinnerComponent, CoverageBarComponent],
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

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      const owner = params.get('owner') ?? '';
      const name = params.get('repo') ?? '';
      this.owner.set(owner);
      this.name.set(name);
      this.repo.set(null);
      this.commits.set(null);
      const [repo, commits] = await Promise.all([
        this.browse.getRepo(owner, name),
        this.browse.getCommits(owner, name),
      ]);
      this.repo.set(repo);
      this.commits.set(commits);
    });
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
}
