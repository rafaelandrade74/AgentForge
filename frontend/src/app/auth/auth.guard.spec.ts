import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from './auth.service';

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
