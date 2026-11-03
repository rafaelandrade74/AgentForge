import { HttpClient } from '@angular/common/http';
import { Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { AuthService } from '../auth/auth.service';

@Component({
  selector: 'app-home',
  standalone: true,
  template: `
    <header class="bar">
      <strong>AgentForge</strong>
      <span>{{ me()?.username }}</span>
      <button type="button" (click)="auth.logout()">Sair</button>
    </header>
    <main><p>Projetos aparecerão aqui nas próximas fases.</p></main>
  `,
  styles: `
    .bar { display: flex; gap: 1rem; align-items: center; padding: .75rem 1rem; border-bottom: 1px solid #333; }
    .bar span { margin-left: auto; }
    main { padding: 1rem; }
  `,
})
export class HomeComponent {
  readonly auth = inject(AuthService);
  readonly me = toSignal(inject(HttpClient).get<{ username: string }>('/api/auth/me'));
}
