import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective } from '@mintplayer/ng-bootstrap/tab-control';
import { BsCodeSnippetComponent } from '@mintplayer/ng-bootstrap/code-snippet';
import { BsTrendChartComponent } from '@mintplayer/ng-bootstrap/charts/trend';
import type { TrendSeries } from '@mintplayer/web-components/charts/trend';
import { BrowseService, CommitInfo, HistoryPoint, RepoInfo, coveragePercent } from '../../services/browse.service';
import { CoverageBarComponent } from '../../components/coverage-bar/coverage-bar.component';

interface WorkflowExample {
  key: string;
  label: string;
  note: string;
  code: string;
  /** Optional per-project configuration shown above the workflow. */
  config?: { note: string; code: string; language: string };
}

@Component({
  selector: 'app-repo',
  imports: [CommonModule, DatePipe, RouterModule, FormsModule, BsCardComponent, BsCardHeaderComponent, BsCardBodyComponent, BsTableComponent, BsBadgeComponent, BsSpinnerComponent, BsSelectComponent, BsSelectOption, BsTabControlComponent, BsTabPageComponent, BsTabPageHeaderDirective, BsCodeSnippetComponent, BsTrendChartComponent, CoverageBarComponent],
  templateUrl: './repo.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class RepoComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);

  readonly owner = signal('');
  readonly name = signal('');
  readonly repo = signal<RepoInfo | null>(null);
  readonly commits = signal<CommitInfo[] | null>(null);
  readonly branches = signal<string[]>([]);
  /** '' = all branches. */
  readonly selectedBranch = signal('');
  readonly history = signal<HistoryPoint[]>([]);

  /**
   * Trend series for bs-trend-chart. Dates are only usable as x when every
   * point has one (pre-FirstSeenAtUtc documents may not) — otherwise fall
   * back to the commit index.
   */
  readonly trendSeries = computed<TrendSeries[]>(() => {
    const history = this.history();
    if (history.length < 2) return [];
    const allDated = history.every((h) => !!h.timestamp);
    return [{
      id: 'coverage',
      label: 'Line coverage %',
      points: history.map((h, i) => ({ x: allDated ? new Date(h.timestamp!) : i, y: h.percent })),
    }];
  });

  /** Example CI workflows per ecosystem, built against this deployment's URL. */
  readonly workflowExamples = computed<WorkflowExample[]>(() => {
    const url = this.repo()?.baseUrl || location.origin;
    const upload = (extra = '') => `      - name: Upload coverage
        uses: MintPlayer/CodeCoverage/action@master
        with:
          url: ${url}
          use-oidc: true${extra}
          finish: true`;
    const header = (testJob: string) => `name: CI
on:
  push:
    branches: [main]
  pull_request:

permissions:
  contents: read
  id-token: write   # tokenless upload via OIDC

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
${testJob}`;

    return [
      {
        key: 'dotnet',
        label: '.NET',
        note: 'Coverlet ships with the xunit/mstest templates; --collect produces a Cobertura report the action auto-detects.',
        code: header(`      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 9.0.x
      - run: dotnet test --collect:"XPlat Code Coverage"
${upload()}`),
      },
      {
        key: 'node',
        label: 'Node.js',
        note: 'Jest writes coverage/lcov.info when run with --coverage; lcov is auto-detected.',
        code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - run: npm ci
      - run: npx jest --coverage
${upload()}`),
      },
      {
        key: 'angular',
        label: 'Angular',
        note: 'ng test --code-coverage emits coverage/<project>/lcov.info via karma-coverage.',
        code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - run: npm ci
      - run: npx ng test --watch=false --code-coverage --browsers=ChromeHeadless
${upload()}`),
      },
      {
        key: 'react',
        label: 'React',
        note: 'Vitest with the v8 provider writes an lcov report; for CRA/jest use "npm test -- --coverage --watchAll=false" instead.',
        code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
      - run: npm ci
      - run: npx vitest run --coverage --coverage.reporter=lcov
${upload()}`),
      },
      {
        key: 'python',
        label: 'Python',
        note: 'pytest-cov with --cov-report=xml produces a Cobertura-style coverage.xml.',
        code: header(`      - uses: actions/setup-python@v5
        with:
          python-version: "3.13"
      - run: pip install -r requirements.txt pytest pytest-cov
      - run: pytest --cov --cov-report=xml
${upload()}`),
      },
      {
        key: 'java',
        label: 'Java',
        note: 'The JaCoCo Maven plugin writes target/site/jacoco/jacoco.xml during verify.',
        code: header(`      - uses: actions/setup-java@v4
        with:
          distribution: temurin
          java-version: "21"
      - run: mvn -B verify
${upload(`
          files: '**/jacoco.xml'`)}`),
      },
      {
        key: 'nx',
        label: 'Nx',
        note: 'Prefer run-many over "nx affected" for the coverage run: unaffected projects emit no report, '
          + 'so an affected upload reads as a coverage drop for everything untouched. The --coverage flag '
          + 'forwards to vitest/jest through every Nx target shape (no "--" separator) — including non-JS '
          + 'targets, where it breaks the command (a dotnet test target chokes on it: --exclude those). '
          + 'And run it on the plain test target, not atomized test-ci targets — those run one spec file '
          + 'each into the same directory and overwrite each other\'s report.',
        config: {
          note: 'Per project, emit lcov into a stable workspace-level folder AND declare that folder as the '
            + 'target\'s outputs — otherwise a cache-restored test run produces no report to upload. '
            + 'Vitest needs both lines below (lcov is not a vitest default); Jest projects only need '
            + '"coverageDirectory" (lcov is a Jest default).',
          language: 'ts',
          code: `// libs/my-lib/vitest.config.ts
export default defineConfig({
  test: {
    coverage: {
      provider: 'v8',
      reporter: ['text', 'lcov'],
      reportsDirectory: '../../coverage/libs/my-lib',
    },
  },
});

// libs/my-lib/project.json — lets Nx restore reports on cache hits
//   "test": {
//     "outputs": ["{workspaceRoot}/coverage/{projectRoot}"],
//     ...
//   }`,
        },
        code: header(`      - uses: actions/setup-node@v4
        with:
          node-version: 22
          cache: npm
      - run: npm ci
      - run: npx nx run-many -t test --coverage
${upload(`
          files: 'coverage/**/lcov.info'`)}`),
      },
    ];
  });

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      const owner = params.get('owner') ?? '';
      const name = params.get('repo') ?? '';
      this.owner.set(owner);
      this.name.set(name);
      this.repo.set(null);
      this.commits.set(null);
      this.branches.set([]);
      this.selectedBranch.set('');
      this.history.set([]);
      const [repo, commits, branches, history] = await Promise.all([
        this.browse.getRepo(owner, name),
        this.browse.getCommits(owner, name),
        this.browse.getBranches(owner, name).catch(() => [] as string[]),
        this.browse.getHistory(owner, name).catch(() => [] as HistoryPoint[]),
      ]);
      this.repo.set(repo);
      this.commits.set(commits);
      this.branches.set(branches);
      this.history.set(history);
    });
  }

  async selectBranch(branch: string): Promise<void> {
    this.selectedBranch.set(branch);
    this.commits.set(null);
    const [commits, history] = await Promise.all([
      this.browse.getCommits(this.owner(), this.name(), branch || undefined),
      this.browse.getHistory(this.owner(), this.name(), branch || undefined).catch(() => [] as HistoryPoint[]),
    ]);
    this.commits.set(commits);
    this.history.set(history);
  }

  /** Delta vs the previous (older) commit in the list, in percent points. */
  delta(index: number): number | null {
    const list = this.commits();
    if (!list || index + 1 >= list.length) return null;
    const current = coveragePercent(list[index].coverage);
    const previous = coveragePercent(list[index + 1].coverage);
    if (current === null || previous === null) return null;
    return current - previous;
  }

  badgeMarkdown(): string {
    const r = this.repo();
    if (!r) return '';
    const origin = r.baseUrl || location.origin;
    const base = `${origin}/badge/${r.owner}/${r.name}.svg`;
    const url = r.isPrivate && r.badgeToken ? `${base}?token=${r.badgeToken}` : base;
    return `[![Coverage](${url})](${origin}/r/${r.owner}/${r.name})`;
  }

  async copyBadge(): Promise<void> {
    await navigator.clipboard.writeText(this.badgeMarkdown());
  }

  async rotateBadgeToken(): Promise<void> {
    const r = this.repo();
    if (!r) return;
    const result = await this.browse.rotateBadgeToken(r.owner, r.name);
    this.repo.set({ ...r, badgeToken: result.badgeToken });
  }
}
