import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../auth/auth.service';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="login-wrap">
      <form class="login-card" (ngSubmit)="submit()">
        <div class="login-brand">
          <div class="brand-icon"><svg width="20" height="20" viewBox="0 0 16 16" fill="none"><path d="M8 1L14.5 4.75V11.25L8 15L1.5 11.25V4.75L8 1Z" stroke="#fff" stroke-width="1.5" fill="none"/><circle cx="8" cy="8" r="2.5" fill="#fff"/></svg></div>
          AgentForge
        </div>
        <div style="text-align:center;font-size:14px;color:var(--text-2);margin-bottom:28px">Faça login para continuar</div>
        <div class="form-group" style="margin-bottom:14px">
          <div class="form-label">Usuário</div>
          <input class="form-input" style="width:100%" name="username" [(ngModel)]="username" autocomplete="username" required placeholder="admin" />
        </div>
        <div class="form-group" style="margin-bottom:20px">
          <div class="form-label">Senha</div>
          <input class="form-input" style="width:100%" name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required placeholder="••••••••" />
        </div>
        @if (error()) {
          <p style="color:var(--danger);margin:0 0 8px;font-size:13px" role="alert">{{ error() }}</p>
        }
        <button class="btn btn-primary" type="submit" [disabled]="busy()" style="width:100%;justify-content:center;padding:10px">Entrar</button>
        <div style="text-align:center;font-size:11px;color:var(--text-2);margin-top:16px">v1.0 · Self-hosted</div>
      </form>
    </div>
  `,
  styles: ``,
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
