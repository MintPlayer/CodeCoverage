import { ChangeDetectionStrategy, Component, computed, input, InputSignal } from '@angular/core';
import type { EntityAttributeDefinition } from '@mintplayer/ng-spark/models';
import type { SparkAttributeColumnRenderer, SparkAttributeDetailRenderer } from '@mintplayer/ng-spark/renderers';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';

/**
 * Spark attribute renderer "app-installed": the GitHub App install state as a
 * badge rather than the word "false", which is what a boolean column renders by
 * default and reads as an error rather than a call to action.
 */
@Component({
  selector: 'app-app-installed-renderer',
  imports: [BsBadgeComponent, TranslateKeyPipe],
  template: `
    @if (installed()) {
      <bs-badge class="text-bg-success text-nowrap">{{ 'app.appInstalled' | t }}</bs-badge>
    } @else {
      <bs-badge class="text-bg-secondary text-nowrap">{{ 'app.appNotInstalled' | t }}</bs-badge>
    }
  `,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AppInstalledRendererComponent implements SparkAttributeColumnRenderer, SparkAttributeDetailRenderer {
  value = input<any>();
  attribute = input<EntityAttributeDefinition | undefined>();
  options = input<Record<string, any> | undefined>();
  formData: InputSignal<Record<string, any>> = input<Record<string, any>>({});
  item = input<any>();

  readonly installed = computed(() => this.value() === true);
}
