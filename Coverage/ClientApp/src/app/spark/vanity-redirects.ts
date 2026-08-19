import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { BrowseService } from '../services/browse.service';

/**
 * The repository and commit pages are the generic Spark detail pages
 * (`spark-po-detail` + a few app panels). These guards keep the older,
 * shareable URLs working — README badge markdown links to /r/{owner}/{name} —
 * by resolving the document id and forwarding into the generic page.
 */
export const repositoryRedirectGuard: CanActivateFn = async (route) => {
  const browse = inject(BrowseService);
  const router = inject(Router);
  const owner = route.paramMap.get('owner') ?? '';
  const name = route.paramMap.get('repo') ?? '';
  try {
    const repo = await browse.getRepo(owner, name);
    // Keep query params (e.g. ?flag=) alive through the redirect.
    return router.createUrlTree(['/po', 'repository', repo.id], { queryParams: route.queryParams });
  } catch {
    return router.createUrlTree(['/home']);
  }
};

export const commitRedirectGuard: CanActivateFn = async (route) => {
  const browse = inject(BrowseService);
  const router = inject(Router);
  const owner = route.paramMap.get('owner') ?? '';
  const name = route.paramMap.get('repo') ?? '';
  const sha = route.paramMap.get('sha') ?? '';
  try {
    const commit = await browse.getCommit(owner, name, sha);
    return router.createUrlTree(['/po', 'commit', commit.id], { queryParams: route.queryParams });
  } catch {
    return router.createUrlTree(['/home']);
  }
};
