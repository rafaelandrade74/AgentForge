import { HttpErrorResponse, HttpInterceptorFn, HttpRequest } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, switchMap, throwError } from 'rxjs';
import { AuthService } from './auth.service';

const withToken = (req: HttpRequest<unknown>, token: string | null) =>
  token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;

const isAuthEndpoint = (url: string) => url.includes('/api/auth/login') || url.includes('/api/auth/refresh');

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (isAuthEndpoint(req.url)) return next(req);

  return next(withToken(req, auth.accessToken)).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || err.status !== 401 || !auth.isAuthenticated()) {
        return throwError(() => err);
      }
      // Token expirado: renova uma vez e repete a requisição; se falhar, volta ao login.
      return auth.refresh().pipe(
        switchMap((token) => next(withToken(req, token))),
        catchError((refreshErr) => {
          auth.logout(router.url);
          return throwError(() => refreshErr);
        }),
      );
    }),
  );
};
