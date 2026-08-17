import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBreadcrumbComponent, BsBreadcrumbItemComponent } from '@mintplayer/ng-bootstrap/breadcrumb';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsHierarchyChartComponent, type HierarchyNodeEventDetail } from '@mintplayer/ng-bootstrap/charts/hierarchy';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BrowseService, CoverageHierarchyNode, TreeResponse } from '../../services/browse.service';
import { CoverageBarComponent } from '../coverage-bar/coverage-bar.component';

/**
 * The "Files" card of a commit — sunburst hierarchy chart + drill-down folder
 * list — shared by the vanity commit page and the generic /po Commit detail
 * page. Self-fetches tree + hierarchy; file clicks open the code viewer.
 */
@Component({
  selector: 'app-commit-files-panel',
  imports: [CommonModule, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, BsAlertComponent, BsBreadcrumbComponent, BsBreadcrumbItemComponent, BsHierarchyChartComponent, BsTableComponent, CoverageBarComponent],
  templateUrl: './commit-files-panel.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class CommitFilesPanelComponent {
  private readonly router = inject(Router);
  private readonly browse = inject(BrowseService);

  owner = input.required<string>();
  name = input.required<string>();
  sha = input.required<string>();

  readonly tree = signal<TreeResponse | null>(null);
  readonly currentPath = signal('');
  readonly hierarchy = signal<CoverageHierarchyNode | null>(null);
  // The chart's zoom root; node ids are repo paths, '/' is the data root.
  readonly chartRootId = signal<string | undefined>('/');
  readonly warningColor = Color.warning;

  /** Segments of the current folder path, each with its cumulative path for the breadcrumb. */
  readonly pathSegments = computed(() => {
    const path = this.currentPath();
    if (!path) return [];
    const segments: { name: string; path: string }[] = [];
    let acc = '';
    for (const part of path.split('/')) {
      acc = acc ? `${acc}/${part}` : part;
      segments.push({ name: part, path: acc });
    }
    return segments;
  });

  constructor() {
    effect(async () => {
      // Re-runs when owner/name/sha change: reset and reload.
      const owner = this.owner();
      const name = this.name();
      const sha = this.sha();
      if (!owner || !name || !sha) return;
      this.tree.set(null);
      this.hierarchy.set(null);
      this.currentPath.set('');
      this.chartRootId.set('/');
      await this.openFolder('');
      try {
        this.hierarchy.set(await this.browse.getHierarchy(owner, name, sha));
      } catch {
        this.hierarchy.set(null);
      }
    });
  }

  async openFolder(path: string): Promise<void> {
    this.currentPath.set(path);
    this.chartRootId.set(path || '/');
    this.tree.set(null);
    try {
      this.tree.set(await this.browse.getTree(this.owner(), this.name(), this.sha(), path || undefined));
    } catch {
      this.tree.set({ buildId: '', entries: [], unmatchedFiles: [] });
    }
  }

  openFile(path: string): void {
    this.router.navigate(['/r', this.owner(), this.name(), 'c', this.sha(), 'f'], { queryParams: { path } });
  }

  // Chart → drill-down sync. Zooming a folder re-roots the chart itself (via
  // [(rootId)]); mirror it into the folder list. Selecting a leaf opens the file.
  onChartZoom(detail: HierarchyNodeEventDetail): void {
    const path = detail.node.id === '/' ? '' : detail.node.id;
    if (path !== this.currentPath()) {
      void this.openFolder(path);
    }
  }

  onChartSelect(detail: HierarchyNodeEventDetail): void {
    this.openFile(detail.node.id);
  }
}
