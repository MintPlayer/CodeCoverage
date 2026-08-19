import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { SparkPoDetailComponent } from '@mintplayer/ng-spark/po-detail';
import { SparkService } from '@mintplayer/ng-spark/services';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { RepoBadgePanelComponent } from '../components/repo-badge-panel/repo-badge-panel.component';
import { RepoGatePanelComponent } from '../components/repo-gate-panel/repo-gate-panel.component';
import { RepoTrendPanelComponent } from '../components/repo-trend-panel/repo-trend-panel.component';
import { RepoSetupPanelComponent } from '../components/repo-setup-panel/repo-setup-panel.component';
import { CommitFilesExtrasComponent } from './commit-files-extras.component';
import { resolveVanityRoute } from './vanity-routes';
import { rowAttr } from './row-attr';

/**
 * The app's poDetail route component (sparkRoutes({ poDetail }) override).
 *
 * Spark's grids and reference links can only emit `/po/{type}/{id}`. For the
 * entity types that own a purpose-built page (Repository, Commit, Account)
 * this forwards there, so a click from any generic grid lands on the real
 * product page. Everything else — and any object whose canonical route can't
 * be derived — renders the stock generic detail, enriched for Repository and
 * Commit through `extraContentTemplate`.
 */
@Component({
  selector: 'app-po-detail-page',
  imports: [SparkPoDetailComponent, BsSpinnerComponent, RepoBadgePanelComponent, RepoGatePanelComponent, RepoTrendPanelComponent, RepoSetupPanelComponent, CommitFilesExtrasComponent],
  template: `
    @if (mode() === 'generic') {
      <spark-po-detail [extraContentTemplate]="extras" />
    } @else {
      <div class="text-center p-5"><bs-spinner /></div>
    }

    <ng-template #extras let-po let-entityType="entityType">
      @if (entityType.name === 'Repository') {
        @if (repoOf(po); as repo) {
          <app-repo-badge-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-gate-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-trend-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-setup-panel />
        }
      } @else if (entityType.name === 'Commit') {
        <app-commit-files-extras [po]="po" />
      }
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoDetailPageComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly spark = inject(SparkService);

  readonly mode = signal<'resolving' | 'generic'>('resolving');

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      this.mode.set('resolving');
      const type = params.get('type') ?? '';
      const id = params.get('id') ?? '';
      const vanity = await this.vanityRoute(type, id);
      if (vanity) {
        // replaceUrl: Back returns to the grid the user came from, not here.
        void this.router.navigate(vanity, { replaceUrl: true });
        return;
      }
      this.mode.set('generic');
    });
  }

  private async vanityRoute(type: string, id: string) {
    if (!type || !id) return null;
    try {
      const entityType = await this.spark.getEntityType(type);
      if (!entityType) return null;
      const po = await this.spark.get(type, id);
      return await resolveVanityRoute(this.spark, entityType.name, po);
    } catch {
      return null;
    }
  }

  repoOf(po: PersistentObject): { owner: string; name: string } | null {
    const fullName = rowAttr(po, 'FullName');
    if (typeof fullName !== 'string') return null;
    const [owner, name] = fullName.split('/');
    return owner && name ? { owner, name } : null;
  }
}
