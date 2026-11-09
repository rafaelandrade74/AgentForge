import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { debounceTime, interval } from 'rxjs';
import { ApiService } from '../core/api.service';
import { usageLimitText } from '../core/usage-limit';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatToolbarModule } from '@angular/material/toolbar';
import { AuthService } from '../auth/auth.service';
import { AgentsService } from '../core/agents.service';
import { RealtimeService } from '../core/realtime.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatButtonModule],
  template: `
    <mat-toolbar>
      <a routerLink="/" class="brand">AgentForge</a>
      <a mat-button routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Projetos</a>
      <a mat-button routerLink="/approvals" routerLinkActive="active" data-testid="nav-approvals">
        Aprovações @if (pending() > 0) { <span class="pending" [attr.aria-label]="pending() + ' aguardando'">{{ pending() }}</span> }
      </a>
      <a mat-button routerLink="/live" routerLinkActive="active" data-testid="nav-live">Ao vivo</a>
      <a mat-button routerLink="/chat" routerLinkActive="active" data-testid="nav-chat">Chat</a>
      <a mat-button routerLink="/agents" routerLinkActive="active">Agentes</a>
      <a mat-button routerLink="/prompts" routerLinkActive="active">Prompts</a>
      <a mat-button routerLink="/environments" routerLinkActive="active">Ambientes</a>
      <a mat-button routerLink="/profiles" routerLinkActive="active">Perfis</a>
      <a mat-button routerLink="/git" routerLinkActive="active">Git</a>
      <a mat-button routerLink="/integration" routerLinkActive="active">Integração</a>
      <span class="spacer"></span>
      <span class="rt" [class.on]="realtime.status() === 'connected'" [title]="'Tempo real: ' + realtime.status()">●</span>
      <span class="user">{{ auth.username() }}</span>
      <button mat-button type="button" (click)="auth.logout()">Sair</button>
    </mat-toolbar>
    @if (agents.needsAttention().length > 0) {
      <div class="banner" role="status">
        <span>
          @for (a of agents.needsAttention(); track a.name; let last = $last) {
            <strong>{{ a.displayName }}</strong> {{ a.installed ? 'não está conectado' : 'não está instalado' }}{{ last ? '' : '; ' }}
          }
          — os papéis que o usam vão falhar.
        </span>
        <a mat-flat-button routerLink="/agents">Configurar agentes</a>
      </div>
    }
    @for (l of agents.usageLimits(); track l.environment + l.agent) {
      <div class="banner limit" role="status" data-testid="usage-limit">
        <span>{{ limitText(l) }} As tarefas ficam em espera e retomam sozinhas.</span>
        <a mat-flat-button routerLink="/environments">Ver tarefas em espera</a>
      </div>
    }
    <router-outlet />
  `,
  styles: `
    mat-toolbar { gap: .5rem; border-bottom: 1px solid #2a2a2a; }
    .brand { font-weight: 700; margin-right: 1rem; text-decoration: none; color: inherit; }
    .spacer { flex: 1; }
    .active { background: #ffffff14; }
    .user { font-size: .9rem; opacity: .8; }
    .pending { display: inline-block; min-width: 1.3rem; padding: 0 .35rem; margin-left: .25rem; border-radius: 999px; background: #c9a227; color: #1a1a1a; font-size: .75rem; font-weight: 700; text-align: center; }
    .rt { color: #777; font-size: .8rem; }
    .rt.on { color: #5fd47a; }
    .banner.limit { background: #3a2a4a; color: #e6d0ff; }
    .banner { display: flex; gap: 1rem; align-items: center; justify-content: center; flex-wrap: wrap; padding: .5rem 1rem; background: #4a3a10; color: #ffe08a; }
  `,
})
export class ShellComponent {
  refreshPending(): void {
    this.api.approvals().subscribe({ next: (a) => this.pending.set(a.length), error: () => undefined });
  }

  readonly auth = inject(AuthService);
  readonly realtime = inject(RealtimeService);
  readonly agents = inject(AgentsService);

  readonly limitText = usageLimitText;
  private readonly api = inject(ApiService);
  /** Quantas tarefas esperam a sua aprovação (selo no menu). */
  readonly pending = signal(0);

  constructor() {
    this.agents.refresh();
    this.agents.refreshLimits();
    this.refreshPending();
    this.realtime.workItemChanged$.pipe(debounceTime(500), takeUntilDestroyed()).subscribe(() => this.refreshPending());
    interval(30_000).pipe(takeUntilDestroyed()).subscribe(() => this.agents.refreshLimits());
  }
}
