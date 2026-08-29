import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { KeyValuePipe } from '@angular/common';
import { NavigationEnd, Router, RouterModule } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map, startWith } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { BsAlertComponent, BsAlertCloseComponent } from '@mintplayer/ng-bootstrap/alert';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { Color } from '@mintplayer/ng-bootstrap';
import { SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective } from '@mintplayer/ng-spark/shell';
import { SparkLanguageService, SparkService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { GitHubLoginService } from '../services/github-login.service';
import { HOME_ROUTE, HOME_URL } from '../spark/home-route';

/**
 * The application frame. All responsive behaviour — breakpoints, the overlay drawer,
 * dismiss-on-navigate, the toggler↔drawer mirror — belongs to `<spark-shell>` and the
 * `mp-shell` web component underneath it; this component owns only what is specific to
 * Coverage: the GitHub sign-in block, the login-error alert and the Resync action.
 *
 * The sidebar menu is server-driven (`GET /spark/program-units`, already rights-filtered
 * per caller), so there are no router links here. A new entry goes in `programUnits.json`.
 */
@Component({
  selector: 'app-shell',
  imports: [
    RouterModule, FormsModule, KeyValuePipe,
    SparkShellComponent, SparkShellTopbarEndDirective, SparkShellMainHeaderDirective,
    BsAlertComponent, BsAlertCloseComponent, BsSelectComponent, BsSelectOption,
    ResolveTranslationPipe, TranslateKeyPipe,
  ],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  private readonly router = inject(Router);
  private readonly spark = inject(SparkService);
  private readonly gitHubLogin = inject(GitHubLoginService);
  readonly authService = inject(SparkAuthService);
  readonly lang = inject(SparkLanguageService);

  loginError = signal<string | null>(null);
  resyncing = signal(false);
  readonly dangerColor = Color.danger;

  /**
   * Resync acts on the accounts grid, which only the Home page renders — showing the button
   * elsewhere would offer an action whose visible effect is on a page you are not looking at.
   */
  readonly onHome = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects.startsWith(HOME_URL)),
      startWith(this.router.url.startsWith(HOME_URL)),
    ),
    { initialValue: false },
  );

  // The flow itself (popup handshake, blocked → redirect fallback, error map)
  // lives in GitHubLoginService, shared with home's reconnect banner. Every
  // failure is surfaced here: a popup that closes with no visible effect
  // reads as "broken".
  async loginWithGitHub(): Promise<void> {
    this.loginError.set(null);
    const result = await this.gitHubLogin.login(HOME_URL);
    if (!result.success) this.loginError.set(result.message ?? null);
  }

  async logout(): Promise<void> {
    await this.authService.logout();
  }

  /**
   * Executes the server-side Resync custom action. The grid updates itself: the action emits a
   * `refreshQuery` client operation, which `provideSparkClientOperations()` dispatches — so this
   * never touches the grid, and works the same from anywhere the action is invoked.
   */
  async resync(): Promise<void> {
    if (this.resyncing()) return;
    this.resyncing.set(true);
    try {
      // (type, action, parent, selectedItemIds, queryParent, queryId). Resync is
      // parentless and selectionless; the query is named only so the server knows
      // which grid the refresh applies to.
      await this.spark.executeCustomAction(
        HOME_ROUTE.accountsType, 'Resync', undefined, [], undefined, HOME_ROUTE.accountsQueryAlias);
    } finally {
      this.resyncing.set(false);
    }
  }

  // bs-alert-close only hides the alert (isVisible model); clear the error so
  // the @if removes it and a later failure starts from a fresh, visible alert.
  onLoginAlertVisible(visible: boolean): void {
    if (!visible) this.loginError.set(null);
  }
}
