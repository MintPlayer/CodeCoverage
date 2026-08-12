import { Component, ChangeDetectionStrategy, inject, signal, effect, afterNextRender, PLATFORM_ID, DestroyRef } from '@angular/core';
import { CommonModule, isPlatformBrowser } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsShellComponent, BsShellSidebarDirective, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import { BsAlertComponent, BsAlertCloseComponent } from '@mintplayer/ng-bootstrap/alert';
import { Color } from '@mintplayer/ng-bootstrap';
import type { ShellStateChangeEventDetail } from '@mintplayer/web-components/shell';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { FormsModule } from '@angular/forms';
import { KeyValuePipe } from '@angular/common';

@Component({
  selector: 'app-shell',
  imports: [CommonModule, RouterModule, BsShellComponent, BsShellSidebarDirective, BsNavbarTogglerComponent, BsAlertComponent, BsAlertCloseComponent, BsSelectComponent, BsSelectOption, ResolveTranslationPipe, TranslateKeyPipe, FormsModule, KeyValuePipe],
  templateUrl: './shell.component.html',
  styleUrl: './shell.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ShellComponent {
  private readonly platformId = inject(PLATFORM_ID);
  private readonly destroyRef = inject(DestroyRef);
  readonly authService = inject(SparkAuthService);
  readonly lang = inject(SparkLanguageService);

  shellState = signal<BsShellState>('auto');
  isSidebarVisible = signal<boolean>(false);
  loginError = signal<string | null>(null);
  readonly dangerColor = Color.danger;

  /** Server error codes → something a human can act on. */
  private static readonly loginErrorMessages: Record<string, string> = {
    email_not_verified: 'GitHub did not attest a verified email for your account. '
      + 'The GitHub App needs the "Email addresses: Read-only" account permission, '
      + 'and your GitHub primary email must be verified.',
    no_login_info: 'The GitHub authorization was cancelled or expired — try again.',
    account_creation_failed: 'Signing in worked but creating your local account failed — check the server logs.',
    popup_closed: 'The sign-in popup was closed before completing.',
  };

  constructor() {
    afterNextRender(() => {
      this.setupResizeListener();
      this.updateSidebarVisibility();
    });
  }

  // ng-spark-auth 22.1.0 owns the whole popup handshake (blocked, closed and
  // server-refused paths all settle the promise). Fall back to a full-page
  // redirect when a popup blocker interferes — in redirect mode the promise
  // never settles, since the document is being replaced. Every other failure
  // is surfaced: a popup that closes with no visible effect reads as "broken".
  async loginWithGitHub(): Promise<void> {
    this.loginError.set(null);
    const result = await this.authService.loginWithProvider('GitHub', { returnUrl: '/home' });
    if (result.success) return;
    if (result.error === 'popup_blocked') {
      await this.authService.loginWithProvider('GitHub', { returnUrl: '/home', mode: 'redirect' });
      return;
    }
    this.loginError.set(
      ShellComponent.loginErrorMessages[result.error ?? ''] ?? `Sign-in failed (${result.error ?? 'unknown error'}).`);
  }

  async logout(): Promise<void> {
    await this.authService.logout();
  }

  // bs-alert-close only hides the alert (isVisible model); clear the error so
  // the @if removes it and a later failure starts from a fresh, visible alert.
  onLoginAlertVisible(visible: boolean): void {
    if (!visible) this.loginError.set(null);
  }

  toggleSidebar(open: boolean) {
    this.shellState.set(open ? 'show' : 'hide');
    this.updateSidebarVisibility();
  }

  // Keep the navbar-toggler in sync with the shell's actual open/closed state
  // (the shell may toggle itself at the breakpoint in 'auto' mode). We only
  // mirror the reflected visibility here — never force show/hide — so 'auto'
  // responsive behaviour is preserved; explicit toggles go through the toggler.
  onShellToggle(detail: ShellStateChangeEventDetail) {
    this.isSidebarVisible.set(detail.open);
  }

  onMenuItemClick() {
    if (this.shellState() !== 'auto') {
      this.shellState.set('hide');
      this.updateSidebarVisibility();
    }
  }

  private setupResizeListener(): void {
    if (!isPlatformBrowser(this.platformId)) {
      return;
    }

    const onResize = () => this.updateSidebarVisibility();
    window.addEventListener('resize', onResize);
    this.destroyRef.onDestroy(() => window.removeEventListener('resize', onResize));
  }

  private updateSidebarVisibility(): void {
    const state = this.shellState();
    let isVisible: boolean;

    if (state === 'show') {
      isVisible = true;
    } else if (state === 'hide') {
      isVisible = false;
    } else {
      isVisible = this.isAboveBreakpoint();
    }

    this.isSidebarVisible.set(isVisible);
  }

  private isAboveBreakpoint(): boolean {
    if (!isPlatformBrowser(this.platformId)) {
      return false;
    }
    // Bootstrap 'md' breakpoint is 768px
    return window.innerWidth >= 768;
  }
}
