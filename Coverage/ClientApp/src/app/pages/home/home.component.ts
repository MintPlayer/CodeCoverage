import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { BsCardComponent, BsCardHeaderComponent } from '@mintplayer/ng-bootstrap/card';
import { BsGridComponent, BsGridRowDirective, BsGridColumnDirective } from '@mintplayer/ng-bootstrap/grid';
import { BsListGroupComponent, BsListGroupItemComponent } from '@mintplayer/ng-bootstrap/list-group';
import { BsBadgeComponent } from '@mintplayer/ng-bootstrap/badge';
import { BsSpinnerComponent } from '@mintplayer/ng-bootstrap/spinner';
import { TranslateKeyPipe } from '@mintplayer/ng-spark/pipes';
import { SparkAuthService } from '@mintplayer/ng-spark-auth/core';
import { AccountsService, AccountInfo } from '../../services/accounts.service';

@Component({
  selector: 'app-home',
  imports: [CommonModule, BsCardComponent, BsCardHeaderComponent, BsGridComponent, BsGridRowDirective, BsGridColumnDirective, BsListGroupComponent, BsListGroupItemComponent, BsBadgeComponent, BsSpinnerComponent, TranslateKeyPipe],
  templateUrl: './home.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export default class HomeComponent {
  private readonly accountsService = inject(AccountsService);
  readonly authService = inject(SparkAuthService);

  readonly accounts = signal<AccountInfo[] | null>(null);
  readonly loading = signal(false);

  constructor() {
    effect(() => {
      if (this.authService.user()?.isAuthenticated) {
        this.loadAccounts();
      } else {
        this.accounts.set(null);
      }
    });
  }

  private async loadAccounts(): Promise<void> {
    this.loading.set(true);
    try {
      this.accounts.set(await this.accountsService.getMyAccounts());
    } catch {
      this.accounts.set([]);
    } finally {
      this.loading.set(false);
    }
  }
}
