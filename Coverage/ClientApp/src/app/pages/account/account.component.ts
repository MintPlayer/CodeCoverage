import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BrowseService, RepoInfo } from '../../services/browse.service';
import { CoverageBarComponent } from '../../components/coverage-bar/coverage-bar.component';

@Component({
  selector: 'app-account',
  imports: [CommonModule, RouterModule, BsCardComponent, BsCardHeaderComponent, BsTableComponent, BsBadgeComponent, BsSpinnerComponent, CoverageBarComponent],
  templateUrl: './account.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class AccountComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);

  readonly login = signal('');
  readonly repos = signal<RepoInfo[] | null>(null);

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      const login = params.get('login') ?? '';
      this.login.set(login);
      this.repos.set(null);
      this.repos.set(await this.browse.getAccountRepos(login));
    });
  }
}
