import { ChangeDetectionStrategy, Component, computed, input, InputSignal } from '@angular/core';
import { RouterModule } from '@angular/router';
import type { EntityAttributeDefinition, PersistentObject } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { rowAttr } from './row-attr';

/**
 * Spark attribute renderer "short-sha": a commit sha as its 7-char short form
 * (master-parity "Latest commit" cell). With the item row context (Spark#245)
 * it links to the vanity commit page, derived from the row's FullName
 * ("owner/name"); without it, plain text.
 */
@Component({
  selector: 'app-short-sha-renderer',
  imports: [RouterModule],
  template: `
    @if (shortSha(); as sha) {
      @if (commitRoute(); as route) {
        <a [routerLink]="route" class="font-monospace small">{{ sha }}</a>
      } @else {
        <span class="font-monospace small">{{ sha }}</span>
      }
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShortShaRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  formData: InputSignal<Record<string, any>> = input<Record<string, any>>({});
  item = input<any>();

  readonly shortSha = computed(() => {
    const value = this.value();
    return typeof value === 'string' && value.length > 0 ? value.substring(0, 7) : null;
  });

  readonly commitRoute = computed(() => {
    const sha = this.value();
    const fullName = rowAttr(this.item(), 'FullName');
    if (typeof sha !== 'string' || typeof fullName !== 'string') return null;
    const [owner, name] = fullName.split('/');
    return owner && name ? ['/r', owner, name, 'c', sha] : null;
  });
}
