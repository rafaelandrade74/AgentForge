import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';

describe('AuthService.freshAccessToken', () => {
  it('devolve o token atual se ainda falta tempo e renova se está para vencer', async () => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const stored = (mins: number) => JSON.stringify({
      accessToken: 'velho', refreshToken: 'r', expiresAtUtc: new Date(Date.now() + mins * 60_000).toISOString(),
    });

    localStorage.setItem('agentforge.tokens', stored(10));
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    expect(await TestBed.inject(AuthService).freshAccessToken()).toBe('velho');

    localStorage.setItem('agentforge.tokens', stored(0.1));
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    const http = TestBed.inject(HttpTestingController);
    const pending = TestBed.inject(AuthService).freshAccessToken();
    http.expectOne('/api/auth/refresh').flush({ accessToken: 'novo', refreshToken: 'r2', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
    expect(await pending).toBe('novo');
    localStorage.clear();
  });
});

describe('authGuard', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient()] });
  });

  const run = () =>
    TestBed.runInInjectionContext(() =>
      authGuard({} as ActivatedRouteSnapshot, { url: '/projetos' } as RouterStateSnapshot),
    );

  it('redireciona para /login guardando a rota original quando não autenticado', () => {
    const result = run() as UrlTree;
    expect(TestBed.inject(Router).serializeUrl(result)).toBe('/login?returnUrl=%2Fprojetos');
  });

  it('libera a rota quando autenticado', () => {
    localStorage.setItem('agentforge.tokens', JSON.stringify({ accessToken: 'a', refreshToken: 'r', expiresAtUtc: '' }));
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient()] });
    expect(TestBed.inject(AuthService).isAuthenticated()).toBeTrue();
    expect(run()).toBeTrue();
  });
});
