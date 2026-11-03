# ADR 0002 — Autenticação mínima

## Decisão
- Usuário administrador único, semeado por `Auth:AdminUsername` / `Auth:AdminPassword`; só o hash BCrypt fica em memória.
- `POST /api/auth/login` devolve JWT (15 min, HS256) e refresh token aleatório (7 dias). `POST /api/auth/refresh` rotaciona: o refresh token usado deixa de valer.
- A API não sobe sem `Auth:JwtSecret` (mínimo de 32 caracteres) e `Auth:AdminPassword`.
- Angular: `AuthService` (signals), `authGuard` com `returnUrl`, interceptor que anexa o token e, num 401, renova uma vez e repete; se falhar, volta ao login.

## Consequências
- Refresh tokens ficam em memória: reiniciar a API desloga os usuários. Passam para o banco na Fase 2.
- Dependências novas (necessárias para o escopo da fase): `BCrypt.Net-Next`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `System.IdentityModel.Tokens.Jwt`.
- Tokens no `localStorage`: aceitável no MVP de usuário único em homelab; cookies HttpOnly ficam como evolução.
