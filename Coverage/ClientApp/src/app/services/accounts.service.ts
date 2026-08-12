import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';

export interface AccountInfo {
  login: string;
  type: string;
  avatarUrl?: string;
  installed: boolean;
  repoCount: number;
  aggregateCoverage?: number;
}

@Injectable({ providedIn: 'root' })
export class AccountsService {
  private readonly http = inject(HttpClient);

  getMyAccounts(): Promise<AccountInfo[]> {
    return firstValueFrom(this.http.get<AccountInfo[]>('/api/me/accounts'));
  }

  /** Drops the server's cached GitHub visibility and returns the fresh list. */
  resync(): Promise<AccountInfo[]> {
    return firstValueFrom(this.http.post<AccountInfo[]>('/api/me/accounts/resync', {}));
  }
}
