import { Routes } from '@angular/router';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { githubProvider, sparkAuthRoutes, withExternalLogin } from '@mintplayer/ng-spark-auth/routes';
import { ShellComponent } from './shell/shell.component';
import { commitRedirectGuard, repositoryRedirectGuard } from './spark/vanity-redirects';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      // GitHub is the only identity here, so only the external-login landing page
      // is mounted. ng-spark-auth 22.3.0 made this opt-in by construction: with no
      // feature the library emits no pages at all, and a page nobody opted into has
      // no reachable import(), so the local-credential bundles are not shipped
      // either. Deliberately omits withLocalLogin() and withRegistration() — the
      // client half of dropping the local-password surface.
      ...sparkAuthRoutes(withExternalLogin(githubProvider())),
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
