import { Routes } from '@angular/router';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { sparkAuthRoutes } from '@mintplayer/ng-spark-auth/routes';
import { ShellComponent } from './shell/shell.component';
import { commitRedirectGuard, repositoryRedirectGuard } from './spark/vanity-redirects';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      ...sparkAuthRoutes(),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'a/:login', loadComponent: () => import('./pages/account/account.component') },
      // Repositories and commits ARE the generic Spark detail pages; these
      // shareable URLs (README badge markdown links to /r/{owner}/{name})
      // resolve the document id and forward there.
      { path: 'r/:owner/:repo', canActivate: [repositoryRedirectGuard], children: [] },
      { path: 'r/:owner/:repo/c/:sha', canActivate: [commitRedirectGuard], children: [] },
      // The code viewer has no persistent object of its own, so it stays a page.
      { path: 'r/:owner/:repo/c/:sha/f', loadComponent: () => import('./pages/file/file.component') },
      // poDetail override: the generic detail page plus the app panels that
      // can't be expressed as attribute renderers (badge, trend chart, CI
      // setup, the commit file tree).
      ...sparkRoutes({ poDetail: () => import('./spark/po-detail-page.component') })
    ]
  }
];
