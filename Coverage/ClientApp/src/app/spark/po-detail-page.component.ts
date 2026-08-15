import { ChangeDetectionStrategy, Component } from '@angular/core';
import { SparkPoDetailComponent } from '@mintplayer/ng-spark/po-detail';
import type { PersistentObject } from '@mintplayer/ng-spark/models';
import { RepoBadgePanelComponent } from '../components/repo-badge-panel/repo-badge-panel.component';
import { RepoTrendPanelComponent } from '../components/repo-trend-panel/repo-trend-panel.component';
import { RepoSetupPanelComponent } from '../components/repo-setup-panel/repo-setup-panel.component';
import { rowAttr } from './row-attr';

/**
 * The app's poDetail route component (sparkRoutes({ poDetail }) override):
 * the stock generic detail page, plus the rich Repository panels — badge,
 * coverage-over-time graph, CI setup instructions — via extraContentTemplate,
 * so /po/repository/... shows the same panels as the vanity /r page.
 * Other entity types render the plain generic detail.
 */
@Component({
  selector: 'app-po-detail-page',
  imports: [SparkPoDetailComponent, RepoBadgePanelComponent, RepoTrendPanelComponent, RepoSetupPanelComponent],
  template: `
    <spark-po-detail [extraContentTemplate]="extras" />

    <ng-template #extras let-po let-entityType="entityType">
      @if (entityType.name === 'Repository') {
        @if (ownerName(po); as repo) {
          <app-repo-badge-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-trend-panel [owner]="repo.owner" [name]="repo.name" />
          <app-repo-setup-panel />
        }
      }
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class PoDetailPageComponent {
  ownerName(po: PersistentObject): { owner: string; name: string } | null {
    const fullName = rowAttr(po, 'FullName');
    if (typeof fullName !== 'string') return null;
    const [owner, name] = fullName.split('/');
    return owner && name ? { owner, name } : null;
  }
}
