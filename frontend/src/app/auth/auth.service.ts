import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, finalize, map, shareReplay, tap } from 'rxjs';

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
