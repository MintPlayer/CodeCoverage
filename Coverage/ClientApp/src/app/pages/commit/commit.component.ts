import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { SparkSubQueryComponent } from '@mintplayer/ng-spark/po-detail';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsBreadcrumbComponent, BsBreadcrumbItemComponent } from '@mintplayer/ng-bootstrap/breadcrumb';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsHierarchyChartComponent, type HierarchyNodeEventDetail } from '@mintplayer/ng-bootstrap/charts/hierarchy';
import { BrowseService, CommitDetail, CoverageHierarchyNode, TreeResponse, coveragePercent } from '../../services/browse.service';
import { CoverageBarComponent } from '../../components/coverage-bar/coverage-bar.component';
import { CoverageRingComponent } from '../../components/coverage-ring/coverage-ring.component';

@Component({
  selector: 'app-commit',
  imports: [CommonModule, RouterModule, BsCardComponent, BsCardHeaderComponent, BsTableComponent, BsBadgeComponent, BsSpinnerComponent, BsAlertComponent, BsBreadcrumbComponent, BsBreadcrumbItemComponent, BsHierarchyChartComponent, SparkSubQueryComponent, CoverageBarComponent, CoverageRingComponent],
  templateUrl: './commit.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class CommitComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly browse = inject(BrowseService);

  readonly owner = signal('');
  readonly name = signal('');
  readonly sha = signal('');
  readonly commit = signal<CommitDetail | null>(null);
  readonly tree = signal<TreeResponse | null>(null);
  readonly currentPath = signal('');
  readonly hierarchy = signal<CoverageHierarchyNode | null>(null);
  // The chart's zoom root; node ids are repo paths, '/' is the data root.
  readonly chartRootId = signal<string | undefined>('/');

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

  readonly percent = computed(() => coveragePercent(this.commit()?.coverage));
  readonly warningColor = Color.warning;

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      const owner = params.get('owner') ?? '';
      const name = params.get('repo') ?? '';
      const sha = params.get('sha') ?? '';
      this.owner.set(owner);
      this.name.set(name);
      this.sha.set(sha);
      this.commit.set(null);
      this.tree.set(null);
      this.hierarchy.set(null);
      this.currentPath.set('');
      this.chartRootId.set('/');
      this.commit.set(await this.browse.getCommit(owner, name, sha));
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
