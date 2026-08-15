import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideAnimations } from '@angular/platform-browser/animations';
import { provideSparkAuth, withSparkAuth } from '@mintplayer/ng-spark-auth';
import { provideSparkAttributeRenderers } from '@mintplayer/ng-spark/renderers';

import { routes } from './app.routes';
import { CoverageBarRendererComponent } from './spark/coverage-bar-renderer.component';
import { CoverageSummaryDetailRendererComponent } from './spark/coverage-summary-detail-renderer.component';
import { CoverageSparklineRendererComponent } from './spark/coverage-sparkline-renderer.component';
import { ShortShaRendererComponent } from './spark/short-sha-renderer.component';
import { BuildSessionsRendererComponent } from './spark/build-sessions-renderer.component';
import { RepoNameRendererComponent } from './spark/repo-name-renderer.component';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(...withSparkAuth()),
    provideAnimations(),
    provideSparkAuth(),
    provideSparkAttributeRenderers([
      {
        name: 'coverage-bar',
        detailComponent: CoverageSummaryDetailRendererComponent,
        columnComponent: CoverageBarRendererComponent,
      },
      {
        name: 'coverage-sparkline',
        detailComponent: CoverageSparklineRendererComponent,
        columnComponent: CoverageSparklineRendererComponent,
      },
      {
        name: 'short-sha',
        detailComponent: ShortShaRendererComponent,
        columnComponent: ShortShaRendererComponent,
      },
      {
        name: 'build-sessions',
        detailComponent: BuildSessionsRendererComponent,
        columnComponent: BuildSessionsRendererComponent,
      },
      {
        name: 'repo-name',
        detailComponent: RepoNameRendererComponent,
        columnComponent: RepoNameRendererComponent,
      },
    ]),
    provideZonelessChangeDetection()
  ]
};
