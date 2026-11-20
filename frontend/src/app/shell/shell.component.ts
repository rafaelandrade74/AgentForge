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
import { ThemeService } from '../core/theme.service';

interface NavItem { path: string; label: string; group: 'main' | 'config'; icon: string; }

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatButtonModule],
  template: `
    <!-- topbar -->
    <header class="topbar">
      <button class="sidebar-toggle" type="button" (click)="sidebarOpen.set(!sidebarOpen())"
        [attr.aria-expanded]="sidebarOpen()" aria-label="Alternar menu">
        <svg width="20" height="20" viewBox="0 0 20 20" fill="none"><path d="M3 5h14M3 10h14M3 15h14" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>
      </button>
      <a routerLink="/" class="brand">
        <svg class="brand-icon" width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="var(--accent)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2L2 7l10 5 10-5-10-5z"/><path d="M2 17l10 5 10-5"/><path d="M2 12l10 5 10-5"/></svg>
        <span>AgentForge</span>
      </a>
      <span class="spacer"></span>

      @for (b of banners(); track b.text) {
        <span class="topbar-banner" [class]="'topbar-banner--' + b.tone">{{ b.text }}</span>
      }

      <span class="rt" [class.on]="realtime.status() === 'connected'" [title]="'Tempo real: ' + realtime.status()">●</span>

      <button class="icon-btn" type="button" (click)="theme.toggle()" [title]="theme.current() === 'dark' ? 'Tema claro' : 'Tema escuro'" aria-label="Alternar tema">
        @if (theme.current() === 'dark') {
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="5"/><path d="M12 1v2M12 21v2M4.22 4.22l1.42 1.42M18.36 18.36l1.42 1.42M1 12h2M21 12h2M4.22 19.78l1.42-1.42M18.36 5.64l1.42-1.42"/></svg>
        } @else {
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12.79A9 9 0 1111.21 3 7 7 0 0021 12.79z"/></svg>
        }
      </button>

      <span class="user-name">{{ auth.username() }}</span>
      <button class="icon-btn" type="button" (click)="auth.logout()" title="Sair" aria-label="Sair">
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 21H5a2 2 0 01-2-2V5a2 2 0 012-2h4M16 17l5-5-5-5M21 12H9"/></svg>
      </button>
    </header>

    <!-- backdrop mobile -->
    @if (sidebarOpen()) {
      <div class="backdrop" (click)="sidebarOpen.set(false)"></div>
    }

    <!-- sidebar -->
    <aside class="sidebar" [class.open]="sidebarOpen()">
      <nav class="nav-section">
        <span class="nav-label">Principal</span>
        @for (item of mainNav; track item.path) {
          <a [routerLink]="item.path" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: item.path === '/' }"
             class="nav-item" (click)="sidebarOpen.set(false)">
            <svg class="nav-icon" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path [attr.d]="item.icon"/></svg>
            <span class="nav-text">{{ item.label }}</span>
            @if (item.path === '/approvals' && pending() > 0) {
              <span class="nav-badge">{{ pending() }}</span>
            }
          </a>
        }
      </nav>
      <nav class="nav-section">
        <span class="nav-label">Configuração</span>
        @for (item of configNav; track item.path) {
          <a [routerLink]="item.path" routerLinkActive="active"
             class="nav-item" (click)="sidebarOpen.set(false)">
            <svg class="nav-icon" width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path [attr.d]="item.icon"/></svg>
            <span class="nav-text">{{ item.label }}</span>
          </a>
        }
      </nav>
      <div class="sidebar-footer">
        <span class="muted small">AgentForge v1.0</span>
      </div>
    </aside>

    <!-- main content -->
    <main class="content" [class.sidebar-open]="sidebarOpen()">
      @if (agents.needsAttention().length > 0) {
        <div class="banner banner--warn" role="status">
          <span>
            @for (a of agents.needsAttention(); track a.name; let last = $last) {
              <strong>{{ a.displayName }}</strong> {{ a.installed ? 'não está conectado' : 'não está instalado' }}{{ last ? '' : '; ' }}
            }
            — os papéis que o usam vão falhar.
          </span>
          <a mat-stroked-button routerLink="/agents" class="banner-btn">Configurar agentes</a>
        </div>
      }
      @for (l of agents.usageLimits(); track l.environment + l.agent) {
        <div class="banner banner--limit" role="status" data-testid="usage-limit">
          <span>{{ limitText(l) }} As tarefas ficam em espera e retomam sozinhas.</span>
          <a mat-stroked-button routerLink="/environments" class="banner-btn">Ver tarefas em espera</a>
        </div>
      }
      <router-outlet />
    </main>
  `,
  styles: `
    /* ── topbar ── */
    .topbar {
      position: fixed;
      top: 0; left: 0; right: 0;
      height: var(--topbar-height);
      display: flex;
      align-items: center;
      gap: .5rem;
      padding: 0 .75rem;
      background: var(--bg-surface-1);
      border-bottom: 1px solid var(--border);
      box-shadow: 0 1px 3px rgba(0,0,0,.1);
      z-index: 200;
    }
    .brand {
      display: inline-flex;
      align-items: center;
      gap: .5rem;
      font-weight: 700;
      font-size: 1rem;
      text-decoration: none;
      color: var(--text-primary);
      letter-spacing: -0.03em;
    }
    .brand-icon { flex-shrink: 0; }
    .spacer { flex: 1; }

    .icon-btn {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 32px; height: 32px;
      background: none;
      border: none;
      border-radius: var(--radius-sm);
      color: var(--text-secondary);
      cursor: pointer;
      transition: background var(--transition), color var(--transition);
    }
    .icon-btn:hover { background: var(--sidebar-hover); color: var(--text-primary); }

    .sidebar-toggle { composes: icon-btn; }
    .sidebar-toggle {
      display: inline-flex;
      align-items: center;
      justify-content: center;
      width: 32px; height: 32px;
      background: none;
      border: none;
      border-radius: var(--radius-sm);
      color: var(--text-secondary);
      cursor: pointer;
      transition: background var(--transition), color var(--transition);
    }
    .sidebar-toggle:hover { background: var(--sidebar-hover); color: var(--text-primary); }

    .topbar-banner {
      display: inline-flex;
      align-items: center;
      padding: .2rem .6rem;
      border-radius: 999px;
      font-size: .7rem;
      font-weight: 600;
    }
    .topbar-banner--warn { background: var(--warning-bg); color: var(--warning); }
    .topbar-banner--limit { background: #1f172e; color: #d8b4fe; }

    .rt { font-size: .6rem; color: var(--text-tertiary); }
    .rt.on { color: var(--success); }
    .user-name { font-size: .8rem; color: var(--text-secondary); }

    /* ── sidebar ── */
    .sidebar {
      position: fixed;
      top: var(--topbar-height);
      left: 0;
      bottom: 0;
      width: var(--sidebar-width);
      background: var(--sidebar-bg);
      border-right: 1px solid var(--sidebar-border);
      overflow-y: auto;
      z-index: 150;
      display: flex;
      flex-direction: column;
      gap: .25rem;
      padding: .75rem 0;
      transform: translateX(-100%);
      transition: transform 200ms ease;
    }
    .sidebar.open { transform: translateX(0); }

    @media (min-width: 1024px) {
      .sidebar { transform: translateX(0); }
      .sidebar-toggle { display: none; }
    }

    .nav-section { padding: 0 .5rem; }
    .nav-section + .nav-section { margin-top: .75rem; }

    .nav-label {
      display: block;
      padding: .25rem .75rem;
      font-size: .65rem;
      font-weight: 600;
      text-transform: uppercase;
      letter-spacing: .06em;
      color: var(--text-tertiary);
    }
    .nav-item {
      display: flex;
      align-items: center;
      gap: .5rem;
      padding: .45rem .75rem;
      border-radius: var(--radius-sm);
      border-left: 3px solid transparent;
      font-size: .85rem;
      font-weight: 500;
      color: var(--text-secondary);
      text-decoration: none;
      transition: background var(--transition), color var(--transition), border-color var(--transition);
      min-height: 36px;
    }
    .nav-item:hover { background: var(--sidebar-hover); color: var(--text-primary); }
    .nav-item.active {
      background: var(--sidebar-active);
      color: var(--accent);
      font-weight: 600;
      border-left-color: var(--accent);
    }
    .nav-icon { width: 18px; height: 18px; flex-shrink: 0; opacity: .6; }
    .nav-item:hover .nav-icon { opacity: .85; }
    .nav-item.active .nav-icon { opacity: 1; color: var(--accent); }
    .nav-text { flex: 1; }
    .sidebar-footer { margin-top: auto; padding: .75rem 1.25rem; border-top: 1px solid var(--sidebar-border); }
    .nav-badge {
      margin-left: auto;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      min-width: 1.1rem;
      height: 1.1rem;
      padding: 0 .3rem;
      border-radius: 999px;
      background: var(--accent);
      color: #fff;
      font-size: .65rem;
      font-weight: 700;
    }

    /* ── backdrop (mobile) ── */
    .backdrop {
      position: fixed;
      inset: 0;
      z-index: 140;
      background: rgba(0,0,0,.4);
    }

    /* ── main content ── */
    .content {
      margin-top: var(--topbar-height);
      min-height: calc(100vh - var(--topbar-height));
      transition: margin-left 200ms ease;
    }
    @media (min-width: 1024px) {
      .content { margin-left: var(--sidebar-width); }
    }

    /* ── banners ── */
    .banner {
      display: flex;
      gap: .75rem;
      align-items: center;
      justify-content: center;
      flex-wrap: wrap;
      padding: .5rem 1rem;
      font-size: .85rem;
    }
    .banner--warn {
      background: var(--warning-bg);
      color: var(--warning);
      border-bottom: 1px solid var(--warning-border);
    }
    .banner--limit {
      background: var(--accent-muted);
      color: var(--accent);
      border-bottom: 1px solid var(--border);
    }
    .banner-btn { font-size: .8rem !important; }

    @media (pointer: coarse) {
      .nav-item { min-height: 44px; padding: .6rem .75rem; }
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
  readonly theme = inject(ThemeService);

  readonly limitText = usageLimitText;
  private readonly api = inject(ApiService);
  readonly pending = signal(0);
  readonly sidebarOpen = signal(false);

  readonly mainNav: NavItem[] = [
    { path: '/', label: 'Projetos', group: 'main', icon: 'M3 7v10a2 2 0 002 2h12a2 2 0 002-2V9a2 2 0 00-2-2h-4.586a1 1 0 01-.707-.293l-1.414-1.414A1 1 0 0010.586 5H5a2 2 0 00-2 2z' },
    { path: '/approvals', label: 'Aprovações', group: 'main', icon: 'M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z' },
    { path: '/live', label: 'Ao vivo', group: 'main', icon: 'M22 12h-4l-3 9L9 3l-3 9H2' },
    { path: '/chat', label: 'Chat', group: 'main', icon: 'M21 11.5a8.38 8.38 0 01-.9 3.8 8.5 8.5 0 01-7.6 4.7 8.38 8.38 0 01-3.8-.9L3 21l1.9-5.7a8.38 8.38 0 01-.9-3.8 8.5 8.5 0 014.7-7.6 8.38 8.38 0 013.8-.9h.5a8.48 8.48 0 018 8v.5z' },
  ];
  readonly configNav: NavItem[] = [
    { path: '/agents', label: 'Agentes', group: 'config', icon: 'M9 3v2m6-2v2M9 19v2m6-2v2M3 9h2m-2 6h2M19 9h2m-2 6h2M7 19h10a2 2 0 002-2V7a2 2 0 00-2-2H7a2 2 0 00-2 2v10a2 2 0 002 2z' },
    { path: '/prompts', label: 'Prompts', group: 'config', icon: 'M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z' },
    { path: '/environments', label: 'Ambientes', group: 'config', icon: 'M5 12h14M12 12v7m-7 0h14a1 1 0 001-1v-2a1 1 0 00-1-1H5a1 1 0 00-1 1v2a1 1 0 001 1zM12 5v7M5 5h14a1 1 0 011 1v2a1 1 0 01-1 1H5a1 1 0 01-1-1V6a1 1 0 011-1z' },
    { path: '/profiles', label: 'Perfis', group: 'config', icon: 'M20 7l-8-4-8 4m16 0l-8 4m8-4v10l-8 4m0-10L4 7m8 4v10M4 7v10l8 4' },
    { path: '/git', label: 'Git', group: 'config', icon: 'M6 3v12M18 9a3 3 0 100-6 3 3 0 000 6zm0 0v3a3 3 0 01-3 3H9m0 0a3 3 0 100 6' },
    { path: '/integration', label: 'Integração', group: 'config', icon: 'M11 4a2 2 0 114 0v1a1 1 0 001 1h3a2 2 0 012 2v3a1 1 0 01-1 1 2 2 0 100 4 1 1 0 011 1v3a2 2 0 01-2 2h-3a1 1 0 01-1-1 2 2 0 10-4 0 1 1 0 01-1 1H7a2 2 0 01-2-2v-3a1 1 0 011-1 2 2 0 100-4 1 1 0 01-1-1V7a2 2 0 012-2h3a1 1 0 001-1V4z' },
  ];

  get banners() {
    return () => {
      const result: { text: string; tone: string }[] = [];
      if (this.agents.needsAttention().length > 0) result.push({ text: 'Agente sem conexão', tone: 'warn' });
      for (const l of this.agents.usageLimits()) result.push({ text: `Limite: ${l.agent}`, tone: 'limit' });
      return result;
    };
  }

  constructor() {
    this.agents.refresh();
    this.agents.refreshLimits();
    this.refreshPending();
    this.realtime.workItemChanged$.pipe(debounceTime(500), takeUntilDestroyed()).subscribe(() => this.refreshPending());
    interval(30_000).pipe(takeUntilDestroyed()).subscribe(() => this.agents.refreshLimits());
  }
}
