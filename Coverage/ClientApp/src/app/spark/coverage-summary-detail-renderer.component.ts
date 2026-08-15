import { ChangeDetectionStrategy, Component, computed, input, InputSignal } from '@angular/core';
import type { EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import type { SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { CoverageSummary } from '../services/browse.service';
import { CoverageBarComponent } from '../components/coverage-bar/coverage-bar.component';
import { CoverageRingComponent } from '../components/coverage-ring/coverage-ring.component';
import { toCoverageSummary } from './coverage-summary';

/**
 * Detail slot of the "coverage-bar" renderer: the ring + bar + line/branch/file
 * counts the commit page used to draw by hand. The column slot stays the
 * compact bar (CoverageBarRendererComponent).
 */
@Component({
  selector: 'app-coverage-summary-detail-renderer',
  imports: [CoverageBarComponent, CoverageRingComponent],
  template: `
    @if (summary(); as s) {
      <div class="d-flex flex-wrap align-items-center gap-3">
        @if (s.linesCoverable > 0) {
          <div style="width: 90px;">
            <app-coverage-ring [value]="s.linesCovered * 100 / s.linesCoverable" />
          </div>
        }
        <div>
          <app-coverage-bar [summary]="s" />
          <div class="small text-muted">
            {{ s.linesCovered }}/{{ s.linesCoverable }} lines · {{ s.branchesCovered }}/{{ s.branchesTotal }} branches · {{ s.filesCount }} files
          </div>
        </div>
      </div>
    } @else {
      <span class="text-muted">—</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageSummaryDetailRendererComponent implements SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  formData: InputSignal<Record<string, any>> = input<Record<string, any>>({});
  item = input<any>();

  readonly summary = computed<CoverageSummary | null>(() => toCoverageSummary(this.value() as PersistentObject));
}
