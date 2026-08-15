import { ChangeDetectionStrategy, Component, computed, input, InputSignal } from '@angular/core';
import type { EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { CoverageSummary } from '../services/browse.service';
import { CoverageBarComponent } from '../components/coverage-bar/coverage-bar.component';

/**
 * Spark attribute renderer for the CoverageSummary AsDetail attributes
 * (Repository.LatestCoverage, Commit.Coverage, Build.Coverage) — one class
 * serves both the column and detail slots. The value arrives either as the
 * AsDetail nested PersistentObject (Spark#241) or, defensively, as a flat
 * camelCase dict; both normalize onto the app's CoverageBarComponent.
 */
@Component({
  selector: 'app-coverage-bar-renderer',
  imports: [CoverageBarComponent],
  template: `<app-coverage-bar [summary]="summary()" />`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageBarRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  formData: InputSignal<Record<string, any>> = input<Record<string, any>>({});

  readonly summary = computed<CoverageSummary | null>(() => {
    const value = this.value();
    if (!value) return null;

    // AsDetail nested PersistentObject: { attributes: [{ name, value }, ...] }
    if (Array.isArray((value as PersistentObject).attributes)) {
      const byName = new Map((value as PersistentObject).attributes.map((a) => [a.name, a.value]));
      return {
        linesCovered: Number(byName.get('LinesCovered') ?? 0),
        linesCoverable: Number(byName.get('LinesCoverable') ?? 0),
        branchesCovered: Number(byName.get('BranchesCovered') ?? 0),
        branchesTotal: Number(byName.get('BranchesTotal') ?? 0),
        filesCount: Number(byName.get('FilesCount') ?? 0),
      };
    }

    // Flat dict (camelCase, the /api wire shape)
    if (typeof value === 'object' && 'linesCoverable' in value) {
      return value as CoverageSummary;
    }
    return null;
  });
}
