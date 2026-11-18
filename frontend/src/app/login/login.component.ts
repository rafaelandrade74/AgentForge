import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../auth/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule],
  template: `
    <form class="login" (ngSubmit)="submit()">
      <h1>AgentForge</h1>
      <p class="subtitle">Faça login para continuar</p>
      <label>
        <span class="label-text">Usuário</span>
        <input name="username" [(ngModel)]="username" autocomplete="username" required />
      </label>
      <label>
        <span class="label-text">Senha</span>
        <input name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required />
      </label>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <button type="submit" [disabled]="busy()">Entrar</button>
    </form>
  `,
  styles: `
    .login {
      display: flex;
      flex-direction: column;
      gap: 1.25rem;
      max-width: 22rem;
      width: 100%;
      margin: 0 auto;
      padding: 2rem 1.25rem;
      padding-top: min(15vh, 8rem);
    }
    h1 {
      text-align: center;
      font-size: 1.75rem;
      letter-spacing: -0.03em;
    }
    .subtitle {
      text-align: center;
      color: var(--text-secondary);
      margin: -.5rem 0 .5rem;
      font-size: .95rem;
    }
    label { display: flex; flex-direction: column; gap: .35rem; }
    .label-text { font-size: .875rem; font-weight: 500; color: var(--text-secondary); }
    input {
      padding: .65rem .75rem;
      font: inherit;
      font-size: 16px;
      background: var(--bg-surface-2);
      color: var(--text-primary);
      border: 1px solid var(--border);
      border-radius: var(--radius-sm);
      transition: border-color var(--transition);
    }
    input:focus { border-color: var(--accent); outline: none; }
    button {
      padding: .65rem 1rem;
      min-height: 44px;
      font: inherit;
      font-weight: 600;
      background: var(--accent);
      color: #111;
      border: none;
      border-radius: var(--radius-sm);
      cursor: pointer;
      transition: background var(--transition), opacity var(--transition);
    }
    button:hover { background: var(--accent-hover); }
    button:active { opacity: .9; }
    button:disabled { opacity: .5; cursor: not-allowed; }
    .error { color: var(--danger); margin: 0; font-size: .9rem; }
  `,
})
export class LoginComponent {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  username = '';
  password = '';
  readonly busy = signal(false);
  readonly error = signal('');

  submit(): void {
    this.busy.set(true);
    this.error.set('');
    this.auth.login(this.username, this.password).subscribe({
      next: () => {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        this.router.navigateByUrl(returnUrl?.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/');
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Usuário ou senha inválidos.');
      },
    });
  }
}
