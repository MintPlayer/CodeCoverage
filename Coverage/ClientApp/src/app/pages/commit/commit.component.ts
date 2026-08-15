import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { SparkSubQueryComponent } from '@mintplayer/ng-spark/po-detail';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BrowseService, CommitDetail, coveragePercent } from '../../services/browse.service';
import { CoverageBarComponent } from '../../components/coverage-bar/coverage-bar.component';
import { CoverageRingComponent } from '../../components/coverage-ring/coverage-ring.component';
import { CommitFilesPanelComponent } from '../../components/commit-files-panel/commit-files-panel.component';

@Component({
  selector: 'app-commit',
  imports: [CommonModule, RouterModule, BsCardComponent, BsCardHeaderComponent, BsBadgeComponent, BsSpinnerComponent, SparkSubQueryComponent, CoverageBarComponent, CoverageRingComponent, CommitFilesPanelComponent],
  templateUrl: './commit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class CommitComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);

  readonly owner = signal('');
  readonly name = signal('');
  readonly sha = signal('');
  readonly commit = signal<CommitDetail | null>(null);

  readonly percent = computed(() => coveragePercent(this.commit()?.coverage));

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      const owner = params.get('owner') ?? '';
      const name = params.get('repo') ?? '';
      const sha = params.get('sha') ?? '';
      this.owner.set(owner);
      this.name.set(name);
      this.sha.set(sha);
      this.commit.set(null);
      this.commit.set(await this.browse.getCommit(owner, name, sha));
    });
  }
}
