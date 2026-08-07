import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { combineLatest } from 'rxjs';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BrowseService, FileDetail, LineCoverageInfo } from '../../services/browse.service';

interface RenderedLine {
  number: number;
  text: string;
  status: 'covered' | 'partial' | 'uncovered' | 'none';
  hits?: number;
  branchInfo?: string;
}

/**
 * Line-by-line coverage view. Plain monospace rendering with a coverage
 * gutter; will adopt the syntax-highlighting bs-code-viewer once it ships in
 * mintplayer-ng-bootstrap (docs/spark-handoff.md).
 */
@Component({
  selector: 'app-file',
  imports: [CommonModule, RouterModule, BsCardComponent, BsCardHeaderComponent, BsSpinnerComponent, BsBadgeComponent],
  templateUrl: './file.component.html',
  styleUrl: './file.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class FileComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);

  readonly owner = signal('');
  readonly name = signal('');
  readonly sha = signal('');
  readonly path = signal('');
  readonly detail = signal<FileDetail | null>(null);
  readonly loading = signal(true);
  readonly targetLine = signal<number | null>(null);

  readonly renderedLines = computed<RenderedLine[]>(() => {
    const detail = this.detail();
    if (!detail) return [];

    const byLine = new Map<number, LineCoverageInfo>(detail.lines.map((l) => [l.number, l]));
    const branchesByLine = new Map<number, { taken: number; total: number }>();
    for (const branch of detail.branches) {
      const entry = branchesByLine.get(branch.line) ?? { taken: 0, total: 0 };
      entry.total++;
      if ((branch.taken ?? 0) > 0) entry.taken++;
      branchesByLine.set(branch.line, entry);
    }

    const sourceLines = detail.source !== null
      ? detail.source.replace(/\r\n/g, '\n').split('\n')
      : Array.from({ length: Math.max(...detail.lines.map((l) => l.number), 0) }, () => '');

    return sourceLines.map((text, index) => {
      const number = index + 1;
      const line = byLine.get(number);
      const branches = branchesByLine.get(number);
      return {
        number,
        text,
        status: !line ? 'none'
          : line.status === 'Covered' ? 'covered'
          : line.status === 'PartiallyCovered' ? 'partial'
          : 'uncovered',
        hits: line?.hits ?? undefined,
        branchInfo: branches ? `${branches.taken}/${branches.total}` : undefined,
      };
    });
  });

  readonly stats = computed(() => {
    const detail = this.detail();
    if (!detail) return null;
    const covered = detail.lines.filter((l) => l.status !== 'NotCovered').length;
    return { covered, coverable: detail.lines.length };
  });

  constructor() {
    combineLatest([this.route.paramMap, this.route.queryParamMap, this.route.fragment])
      .pipe(takeUntilDestroyed())
      .subscribe(async ([params, query, fragment]) => {
        const owner = params.get('owner') ?? '';
        const name = params.get('repo') ?? '';
        const sha = params.get('sha') ?? '';
        const path = query.get('path') ?? '';
        this.targetLine.set(fragment?.startsWith('L') ? parseInt(fragment.slice(1), 10) || null : null);

        if (owner === this.owner() && name === this.name() && sha === this.sha() && path === this.path()) {
          this.scrollToTarget();
          return;
        }

        this.owner.set(owner);
        this.name.set(name);
        this.sha.set(sha);
        this.path.set(path);
        this.loading.set(true);
        this.detail.set(null);
        try {
          this.detail.set(await this.browse.getFile(owner, name, sha, path));
        } finally {
          this.loading.set(false);
          setTimeout(() => this.scrollToTarget());
        }
      });
  }

  private scrollToTarget(): void {
    const line = this.targetLine();
    if (line === null) return;
    document.getElementById(`L${line}`)?.scrollIntoView({ block: 'center' });
  }
}
