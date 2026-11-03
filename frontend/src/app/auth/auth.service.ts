import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, finalize, firstValueFrom, map, shareReplay, tap } from 'rxjs';

interface TokenPair {
  accessToken: string;
  refreshToken: string;
  expiresAtUtc: string;
}

const STORAGE_KEY = 'agentforge.tokens';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  private readonly tokens = signal<TokenPair | null>(this.load());
  private refreshing$: Observable<string> | null = null;

  readonly isAuthenticated = computed(() => this.tokens() !== null);

  get accessToken(): string | null {
    return this.tokens()?.accessToken ?? null;
  }

  login(username: string, password: string): Observable<void> {
    return this.http
      .post<TokenPair>('/api/auth/login', { username, password })
      .pipe(
        tap((t) => this.store(t)),
        map(() => undefined),
      );
  }

  /** Renova o token; chamadas simultâneas compartilham a mesma requisição. */
  refresh(): Observable<string> {
    const current = this.tokens();
    if (!current) {
      throw new Error('sem refresh token');
    }
    this.refreshing$ ??= this.http
      .post<TokenPair>('/api/auth/refresh', { refreshToken: current.refreshToken })
      .pipe(
        tap((t) => this.store(t)),
        map((t) => t.accessToken),
        finalize(() => (this.refreshing$ = null)),
        shareReplay(1),
      );
    return this.refreshing$;
  }

  /** Token de acesso válido para abrir/reabrir a conexão do SignalR: renova antes se estiver para vencer. */
  async freshAccessToken(): Promise<string> {
    const current = this.tokens();
    if (!current) return '';
    const expiresAt = Date.parse(current.expiresAtUtc);
    if (Number.isNaN(expiresAt) || expiresAt - Date.now() > 30_000) return current.accessToken;
    try {
      return await firstValueFrom(this.refresh());
    } catch {
      this.logout();
      return '';
    }
  }

  username(): string | null {
    const token = this.accessToken;
    if (!token) return null;
    try {
      const payload = JSON.parse(atob(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
      return payload.sub ?? payload.unique_name ?? null;
    } catch {
      return null;
    }
  }

  logout(returnUrl?: string): void {
    this.store(null);
    this.router.navigate(['/login'], returnUrl ? { queryParams: { returnUrl } } : {});
  }

  private store(t: TokenPair | null): void {
    this.tokens.set(t);
    try {
      if (t) localStorage.setItem(STORAGE_KEY, JSON.stringify(t));
      else localStorage.removeItem(STORAGE_KEY);
    } catch {
      // armazenamento indisponível: a sessão vale só até recarregar a página
    }
  }

  private load(): TokenPair | null {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      return raw ? (JSON.parse(raw) as TokenPair) : null;
    } catch {
      return null;
    }
  }
}
