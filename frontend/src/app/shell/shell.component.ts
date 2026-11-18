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
    <nav class="topbar">
      <a routerLink="/" class="brand">AgentForge</a>

      <button class="hamburger" type="button" (click)="menuOpen.set(!menuOpen())"
        [attr.aria-expanded]="menuOpen()" aria-label="Abrir menu">
        <span class="hamburger-line"></span>
        <span class="hamburger-line"></span>
        <span class="hamburger-line"></span>
      </button>

      <div class="nav-links" [class.open]="menuOpen()" (click)="menuOpen.set(false)">
        <a routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Projetos</a>
        <a routerLink="/approvals" routerLinkActive="active" data-testid="nav-approvals">
          Aprovações @if (pending() > 0) { <span class="pending" [attr.aria-label]="pending() + ' aguardando'">{{ pending() }}</span> }
        </a>
        <a routerLink="/live" routerLinkActive="active" data-testid="nav-live">Ao vivo</a>
        <a routerLink="/chat" routerLinkActive="active" data-testid="nav-chat">Chat</a>
        <a routerLink="/agents" routerLinkActive="active">Agentes</a>
        <a routerLink="/prompts" routerLinkActive="active">Prompts</a>
        <a routerLink="/environments" routerLinkActive="active">Ambientes</a>
        <a routerLink="/profiles" routerLinkActive="active">Perfis</a>
        <a routerLink="/git" routerLinkActive="active">Git</a>
        <a routerLink="/integration" routerLinkActive="active">Integração</a>
      </div>

      <span class="spacer"></span>
      <span class="rt" [class.on]="realtime.status() === 'connected'" [title]="'Tempo real: ' + realtime.status()">●</span>
      <span class="user">{{ auth.username() }}</span>
      <button mat-button type="button" (click)="auth.logout()">Sair</button>
    </nav>

    @if (menuOpen()) {
      <div class="backdrop" (click)="menuOpen.set(false)"></div>
    }

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
    /* ── topbar ── */
    .topbar {
      display: flex;
      align-items: center;
      gap: .5rem;
      padding: 0 1rem;
      height: 56px;
      border-bottom: 1px solid var(--border);
      background: var(--bg-surface-1);
      position: sticky;
      top: 0;
      z-index: 100;
    }
    .brand {
      font-weight: 700;
      font-size: 1.05rem;
      margin-right: .75rem;
      text-decoration: none;
      color: var(--text-primary);
      letter-spacing: -0.02em;
    }
    .spacer { flex: 1; }

    /* ── desktop nav links ── */
    .nav-links {
      display: flex;
      gap: .25rem;
      align-items: center;
    }
    .nav-links a {
      display: inline-flex;
      align-items: center;
      padding: .4rem .65rem;
      border-radius: var(--radius-sm);
      font-size: .875rem;
      font-weight: 500;
      color: var(--text-secondary);
      text-decoration: none;
      transition: background var(--transition), color var(--transition);
      white-space: nowrap;
    }
    .nav-links a:hover { background: rgba(255,255,255,.06); color: var(--text-primary); }
    .nav-links a.active { background: rgba(255,255,255,.08); color: var(--text-primary); }

    .pending {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 1.25rem;
      height: 1.25rem;
      padding: 0 .35rem;
      margin-left: .3rem;
      border-radius: 999px;
      background: var(--warning);
      color: #1a1a1a;
      font-size: .7rem;
      font-weight: 700;
    }
    .rt { color: var(--text-tertiary); font-size: .75rem; }
    .rt.on { color: var(--success); }
    .user { font-size: .85rem; color: var(--text-secondary); }

    /* ── hamburger (hidden on desktop) ── */
    .hamburger {
      display: none;
      flex-direction: column;
      justify-content: center;
      gap: 4px;
      width: 36px;
      height: 36px;
      padding: 6px;
      background: none;
      border: none;
      cursor: pointer;
      border-radius: var(--radius-sm);
      transition: background var(--transition);
    }
    .hamburger:hover { background: rgba(255,255,255,.06); }
    .hamburger-line {
      display: block;
      width: 100%;
      height: 2px;
      background: var(--text-secondary);
      border-radius: 1px;
      transition: background var(--transition);
    }

    .backdrop { display: none; }

    /* ── mobile ── */
    @media (max-width: 1023px) {
      .hamburger { display: flex; }

      .nav-links {
        display: none;
        position: fixed;
        top: 56px;
        left: 0;
        right: 0;
        bottom: 0;
        z-index: 200;
        flex-direction: column;
        align-items: stretch;
        gap: 0;
        padding: .75rem;
        background: var(--bg-surface-1);
        overflow-y: auto;
        -webkit-overflow-scrolling: touch;
      }
      .nav-links.open { display: flex; }
      .nav-links a {
        padding: .75rem 1rem;
        font-size: 1rem;
        border-radius: var(--radius-md);
        min-height: 44px;
      }

      .backdrop {
        display: block;
        position: fixed;
        inset: 0;
        z-index: 99;
        background: rgba(0,0,0,.5);
      }
    }

    /* ── banners ── */
    .banner {
      display: flex;
      gap: .75rem;
      align-items: center;
      justify-content: center;
      flex-wrap: wrap;
      padding: .6rem 1rem;
      background: var(--warning-bg);
      color: var(--warning);
      border-bottom: 1px solid var(--warning-border);
      font-size: .9rem;
    }
    .banner.limit {
      background: #1f172e;
      color: #d8b4fe;
      border-bottom-color: #3b2667;
    }
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
  readonly pending = signal(0);
  readonly menuOpen = signal(false);

  constructor() {
    this.agents.refresh();
    this.agents.refreshLimits();
    this.refreshPending();
    this.realtime.workItemChanged$.pipe(debounceTime(500), takeUntilDestroyed()).subscribe(() => this.refreshPending());
    interval(30_000).pipe(takeUntilDestroyed()).subscribe(() => this.agents.refreshLimits());
  }
}
