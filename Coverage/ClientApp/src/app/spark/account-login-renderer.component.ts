import { ChangeDetectionStrategy, Component, computed, input, InputSignal } from '@angular/core';
import { RouterLink } from '@angular/router';
import type { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { rowAttr } from './row-attr';

/**
 * Spark attribute renderer "account-login": avatar plus a link to the app's own
 * account page.
 *
 * The datatable links its first column at `/po/{alias}/{id}` when the row is
 * readable, which is the wrong destination here — the app has a real account
 * page — and a dead one for a row we synthesized for an owner with no Account
 * document yet. Rendering the link ourselves fixes both.
 */
@Component({
  selector: 'app-account-login-renderer',
  imports: [RouterLink],
  template: `
    <span class="d-inline-flex align-items-center gap-2">
      @if (avatarUrl()) {
        <img [src]="avatarUrl()" [alt]="value()" width="20" height="20" class="rounded flex-shrink-0">
      } @else {
        <i class="bi bi-person-circle flex-shrink-0" aria-hidden="true"></i>
      }
      <a [routerLink]="['/a', value()]" class="text-truncate">{{ value() }}</a>
    </span>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AccountLoginRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  formData: InputSignal<Record<string, any>> = input<Record<string, any>>({});
  item = input<any>();

  readonly avatarUrl = computed(() => rowAttr(this.item(), 'AvatarUrl') as string | undefined);
}
