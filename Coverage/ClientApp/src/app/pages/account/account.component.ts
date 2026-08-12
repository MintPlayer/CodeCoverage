import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { CommonModule, DatePipe } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsTableComponent } from '@mintplayer/ng-bootstrap/table';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsFormComponent, BsFormControlDirective } from '@mintplayer/ng-bootstrap/form';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { BsSparklineComponent } from '@mintplayer/ng-bootstrap/charts/sparkline';
import { Color } from '@mintplayer/ng-bootstrap';
import { BrowseService, RepoInfo } from '../../services/browse.service';
import { TokensService, TokenInfo, CreatedToken } from '../../services/tokens.service';
import { CoverageBarComponent } from '../../components/coverage-bar/coverage-bar.component';

@Component({
  selector: 'app-account',
  imports: [CommonModule, DatePipe, RouterModule, FormsModule, BsCardComponent, BsCardHeaderComponent, BsTableComponent, BsBadgeComponent, BsSpinnerComponent, BsAlertComponent, BsFormComponent, BsFormControlDirective, BsSelectComponent, BsSelectOption, BsSparklineComponent, CoverageBarComponent],
  templateUrl: './account.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class AccountComponent {
  private readonly route = inject(ActivatedRoute);
  private readonly browse = inject(BrowseService);
  private readonly tokensService = inject(TokensService);

  readonly login = signal('');
  readonly repos = signal<RepoInfo[] | null>(null);
  /** fullName → recent coverage percentages (ascending) for table sparklines. */
  readonly sparklines = signal<Record<string, number[]>>({});

  // Token management: visible only when the tokens list loads (the server
  // returns 403 for accounts the viewer can't manage — that's the gate).
  readonly tokens = signal<TokenInfo[] | null>(null);
  readonly canManageTokens = signal(false);
  readonly newDescription = signal('');
  readonly newRepoFullName = signal('');
  readonly createdToken = signal<CreatedToken | null>(null);
  readonly creating = signal(false);

  readonly successColor = Color.success;

  constructor() {
    this.route.paramMap.pipe(takeUntilDestroyed()).subscribe(async (params) => {
      const login = params.get('login') ?? '';
      this.login.set(login);
      this.repos.set(null);
      this.tokens.set(null);
      this.canManageTokens.set(false);
      this.createdToken.set(null);
      this.sparklines.set({});
      this.repos.set(await this.browse.getAccountRepos(login));
      this.browse.getSparklines(login).then(
        (lines) => this.sparklines.set(lines),
        () => this.sparklines.set({}));
      await this.loadTokens();
    });
  }

  private async loadTokens(): Promise<void> {
    try {
      this.tokens.set(await this.tokensService.list(this.login()));
      this.canManageTokens.set(true);
    } catch {
      this.tokens.set(null);
      this.canManageTokens.set(false);
    }
  }

  async createToken(): Promise<void> {
    this.creating.set(true);
    try {
      const created = await this.tokensService.create(
        this.login(),
        this.newDescription() || null,
        this.newRepoFullName() || null);
      this.createdToken.set(created);
      this.newDescription.set('');
      this.newRepoFullName.set('');
      await this.loadTokens();
    } finally {
      this.creating.set(false);
    }
  }

  async copyToken(): Promise<void> {
    const created = this.createdToken();
    if (created) await navigator.clipboard.writeText(created.tokenValue);
  }

  async revokeToken(token: TokenInfo): Promise<void> {
    await this.tokensService.revoke(token.id);
    await this.loadTokens();
  }
}
