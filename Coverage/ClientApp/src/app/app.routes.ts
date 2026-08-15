import { Routes } from '@angular/router';
import { sparkRoutes } from '@mintplayer/ng-spark/routes';
import { sparkAuthRoutes } from '@mintplayer/ng-spark-auth/routes';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  {
    path: '',
    component: ShellComponent,
    children: [
      ...sparkAuthRoutes(),
      { path: '', redirectTo: 'home', pathMatch: 'full' },
      { path: 'home', loadComponent: () => import('./pages/home/home.component') },
      { path: 'a/:login', loadComponent: () => import('./pages/account/account.component') },
      { path: 'r/:owner/:repo', loadComponent: () => import('./pages/repo/repo.component') },
      { path: 'r/:owner/:repo/c/:sha', loadComponent: () => import('./pages/commit/commit.component') },
      { path: 'r/:owner/:repo/c/:sha/f', loadComponent: () => import('./pages/file/file.component') },
      // poDetail override: the generic detail page plus the rich Repository
      // panels (badge / trend graph / setup), so /po/repository/... renders
      // the same panels as the vanity /r page.
      ...sparkRoutes({ poDetail: () => import('./spark/po-detail-page.component') })
    ]
  }
];
