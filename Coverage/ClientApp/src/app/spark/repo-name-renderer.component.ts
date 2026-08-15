import { ChangeDetectionStrategy, Component, computed, input, InputSignal } from '@angular/core';
import type { EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { rowAttr } from './row-attr';

/**
 * Spark attribute renderer "repo-name": the repository name with master's
 * inline "private" badge — a cross-field cell (Name + IsPrivate) enabled by
 * the item row context (Spark#245).
 */
@Component({
  selector: 'app-repo-name-renderer',
  imports: [BsBadgeComponent],
  template: `
    {{ value() }}
    @if (isPrivate()) {
      <bs-badge class="text-bg-secondary ms-2">private</bs-badge>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class RepoNameRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  formData: InputSignal<Record<string, any>> = input<Record<string, any>>({});
  item = input<any>();

  readonly isPrivate = computed(() => rowAttr(this.item(), 'IsPrivate') === true);
}
