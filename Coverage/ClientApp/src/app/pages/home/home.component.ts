import { ChangeDetectionStrategy, Component, effect, inject, isDevMode, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { Color } from '@mintplayer/ng-bootstrap';
import { BsAlertComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkSubQueryComponent } from '@mintplayer/ng-spark/po-detail';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { AccountsService, AccountsResponse } from '../../services/accounts.service';
import { GitHubLoginService } from '../../services/github-login.service';

@Component({
  selector: 'app-home',
  imports: [CommonModule, RouterModule, BsAlertComponent, BsCardComponent, BsCardHeaderComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsListGroupComponent, BsListGroupItemComponent, SparkSubQueryComponent, TranslateKeyPipe],
  templateUrl: './home.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class HomeComponent {
  private readonly accountsService = inject(AccountsService);
  private readonly gitHubLogin = inject(GitHubLoginService);
  readonly authService = inject(SparkAuthService);

  readonly gitHubAppUrl = signal(`https://github.com/apps/${isDevMode() ? 'coveragedevelopment' : 'coverageproduction'}`);
  readonly loading = signal(false);
  readonly reauthRequired = signal(false);
  readonly reconnecting = signal(false);
  readonly reconnectError = signal<string | null>(null);
  readonly warningColor = Color.warning;

  /**
   * Bumped by {@link resync} to remount the grid. spark-sub-query fetches on
   * mount and exposes no refresh input, so without this a resync would clear the
   * server-side snapshot and leave the old rows on screen — the button would
   * look like it had done nothing.
   */
  readonly gridEpoch = signal(0);

  constructor() {
    effect(() => {
      if (this.authService.user()?.isAuthenticated) {
        this.loadAccounts();
      } else {
        this.reauthRequired.set(false);
      }
    });
  }

  private applyResponse(response: AccountsResponse): void {
    this.gitHubAppUrl.set(response.gitHubAppUrl);
    this.reauthRequired.set(response.gitHubReauthRequired ?? false);
  }

  private async loadAccounts(): Promise<void> {
    this.loading.set(true);
    try {
      this.applyResponse(await this.accountsService.getMyAccounts());
    } catch {
      // The grid reports its own failures; this call only carries the App URL
      // and the reauth flag, and a stale App URL is better than a blank page.
    } finally {
      this.loading.set(false);
    }
  }

  async resync(): Promise<void> {
    this.loading.set(true);
    try {
      this.applyResponse(await this.accountsService.resync());
      this.gridEpoch.update(e => e + 1);
    } catch {
      // keep the current list on failure
    } finally {
      this.loading.set(false);
    }
  }

  // Button-gated on purpose: popups must be user-gesture-initiated, and
  // calling loginWithProvider from the auth effect would re-enter forever.
  // A successful popup re-authorizes AND re-saves fresh tokens (the Spark
  // callback overwrites stored tokens on every success), so resync() then
  // rebuilds visibility with a working token.
  async reconnect(): Promise<void> {
    this.reconnectError.set(null);
    this.reconnecting.set(true);
    try {
      const result = await this.gitHubLogin.login('/home');
      if (result.success) {
        await this.resync();
        return;
      }
      if (result.error === 'popup_closed') return; // "not now" — no error banner
      this.reconnectError.set(result.message ?? null);
    } finally {
      this.reconnecting.set(false);
    }
  }
}
