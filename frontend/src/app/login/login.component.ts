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
      <label>Usuário <input name="username" [(ngModel)]="username" autocomplete="username" required /></label>
      <label>
        Senha
        <input name="password" type="password" [(ngModel)]="password" autocomplete="current-password" required />
      </label>
      @if (error()) {
        <p class="error" role="alert">{{ error() }}</p>
      }
      <button type="submit" [disabled]="busy()">Entrar</button>
    </form>
  `,
  styles: `
    .login { display: flex; flex-direction: column; gap: 1rem; max-width: 20rem; margin: 15vh auto; }
    label { display: flex; flex-direction: column; gap: .25rem; }
    input, button { padding: .5rem; font: inherit; }
    .error { color: #ff6b6b; margin: 0; }
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
        // só aceita caminhos internos, para evitar redirecionamento aberto
        this.router.navigateByUrl(returnUrl?.startsWith('/') && !returnUrl.startsWith('//') ? returnUrl : '/');
      },
      error: () => {
        this.busy.set(false);
        this.error.set('Usuário ou senha inválidos.');
      },
    });
  }
}
