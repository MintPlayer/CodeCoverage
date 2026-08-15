import { ChangeDetectionStrategy, Component, computed, input, InputSignal } from '@angular/core';
import type { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';

/**
 * Spark attribute renderer "short-sha": renders a commit sha as its 7-char
 * short form (master-parity for the "Latest commit" column). Becomes a link
 * once the row-context seam ships (Spark#245) — the full row is needed to
 * build the commit route.
 */
@Component({
  selector: 'app-short-sha-renderer',
  template: `
    @if (shortSha(); as sha) {
      <span class="font-monospace small">{{ sha }}</span>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShortShaRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  formData: InputSignal<Record<string, any>> = input<Record<string, any>>({});

  readonly shortSha = computed(() => {
    const value = this.value();
    return typeof value === 'string' && value.length > 0 ? value.substring(0, 7) : null;
  });
}
