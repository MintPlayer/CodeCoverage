import { Component, ChangeDetectionStrategy, inject, signal, effect, afterNextRender, PLATFORM_ID, DestroyRef } from '@angular/core';
import { CommonModule, isPlatformBrowser } from '@angular/common';
import { RouterModule } from '@angular/router';
import { BsShellComponent, BsShellSidebarDirective, BsShellState } from '@mintplayer/ng-bootstrap/shell';
import { BsNavbarTogglerComponent } from '@mintplayer/ng-bootstrap/navbar-toggler';
import type { ShellStateChangeEventDetail } from '@mintplayer/web-components/shell';
import { BsShellTopbarDirective } from './bs-shell-topbar.directive';
import { BsSelectComponent, BsSelectOption } from '@mintplayer/ng-bootstrap/select';
import { SparkLanguageService } from '@mintplayer/ng-spark/services';
import { ResolveTranslationPipe, TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { FormsModule } from '@angular/forms';
import { KeyValuePipe } from '@angular/common';

@Component({
  selector: 'app-shell',
  imports: [CommonModule, RouterModule, BsShellComponent, BsShellSidebarDirective, BsShellTopbarDirective, BsNavbarTogglerComponent, BsSelectComponent, BsSelectOption, ResolveTranslationPipe, TranslateKeyPipe, FormsModule, KeyValuePipe],
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

  constructor() {
    afterNextRender(() => {
      this.setupResizeListener();
      this.updateSidebarVisibility();
    });
  }

  // Full-page redirect instead of a popup: the Spark external-login callback
  // only emits the popup postMessage handshake when ?popup is propagated, which
  // the /spark/auth/external-login endpoint currently doesn't do. The redirect
  // flow round-trips through GitHub and lands back on returnUrl, signed in.
  loginWithGitHub(): void {
    window.location.href = '/spark/auth/external-login?provider=GitHub&returnUrl=/home';
  }

  async logout(): Promise<void> {
    await this.authService.logout();
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
