import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { arcPath, colorScale } from '@mintplayer/web-components/charts/core';

const RING_INNER = 72;
const RING_OUTER = 94;
const TAU = 2 * Math.PI;

// Same ramp domain as the sunburst (colorMin/colorMax 60–80) so the headline
// number and the chart tell one color story.
const fill = colorScale(60, 80, '#fe0000', '#21b577');

/**
 * Headline coverage ring, hand-rolled on ng-bootstrap's public charts/core
 * (arcPath emits the two-half-arc form, so 100% doesn't vanish; ringGap 0
 * because the default 1px gap is for stacked sunburst rings). A donut/gauge
 * component was explicitly declined upstream — candidate for a later
 * mp-progress-circle PR if this shape proves general.
 */
@Component({
  selector: 'app-coverage-ring',
  imports: [DecimalPipe],
  template: `
    <svg viewBox="0 0 200 200" role="img"
         [attr.aria-label]="'Coverage ' + (value() | number:'1.1-1') + ' percent'">
      <path [attr.d]="track" fill="var(--bs-secondary-bg)" />
      <path [attr.d]="arc()" [attr.fill]="color()" />
      <text x="100" y="100" text-anchor="middle" dominant-baseline="middle"
            font-size="34" fill="var(--bs-body-color)">{{ value() | number:'1.1-1' }}%</text>
    </svg>
  `,
  styles: `:host { display: block; } svg { display: block; width: 100%; height: auto; }`,
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CoverageRingComponent {
  readonly value = input.required<number>();

  protected readonly track = arcPath(100, 100, RING_INNER, RING_OUTER, 0, TAU, { ringGap: 0 });
  protected readonly arc = computed(() =>
    arcPath(100, 100, RING_INNER, RING_OUTER, 0, (this.value() / 100) * TAU, { ringGap: 0 }));
  protected readonly color = computed(() => fill(this.value()));
}
