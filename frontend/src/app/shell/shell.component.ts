import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { debounceTime, interval } from 'rxjs';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { ApiService } from '../core/api.service';
import { usageLimitText } from '../core/usage-limit';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatToolbarModule } from '@angular/material/toolbar';
import { AuthService } from '../auth/auth.service';
import { AgentsService } from '../core/agents.service';
import { RealtimeService } from '../core/realtime.service';
import { ThemeService } from '../core/theme.service';

interface NavItem { path: string; label: string; group: 'main' | 'config'; svgContent: SafeHtml; }

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
        <div class="brand-icon">
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none"><path d="M8 1L14.5 4.75V11.25L8 15L1.5 11.25V4.75L8 1Z" stroke="#fff" stroke-width="1.5" fill="none"/><circle cx="8" cy="8" r="2.5" fill="#fff"/></svg>
        </div>
        <span>AgentForge</span>
      </a>
      <span class="topbar-spacer"></span>

      @for (b of banners(); track b.text) {
        <span class="topbar-banner" [class]="'topbar-banner--' + b.tone">{{ b.text }}</span>
      }

      <div class="env-pill">
        <span class="env-dot" [class.on]="realtime.status() === 'connected'"></span>
        <span>{{ realtime.status() === 'connected' ? 'Conectado' : 'Desconectado' }}</span>
      </div>

      <button class="topbar-btn" type="button" title="Notificações" aria-label="Notificações">
        <svg width="16" height="16" viewBox="0 0 16 16"><path d="M8 14.5c.83 0 1.5-.67 1.5-1.5h-3c0 .83.67 1.5 1.5 1.5zm4.5-4V7c0-2.34-1.6-4.3-3.75-4.82V1.75a.75.75 0 00-1.5 0v.43C5.1 2.7 3.5 4.66 3.5 7v3.5L2 12v.5h12V12l-1.5-1.5z"/></svg>
      </button>

      <button class="topbar-btn" type="button" (click)="theme.toggle()"
        [title]="theme.current() === 'dark' ? 'Tema claro' : 'Tema escuro'" aria-label="Alternar tema">
        @if (theme.current() === 'dark') {
          <svg width="16" height="16" viewBox="0 0 16 16"><circle cx="8" cy="8" r="3.5" fill="currentColor"/><path d="M8 2v1.5m0 9V14m-4.24-9.74l1.06 1.06m6.36 6.36l1.06 1.06M2 8h1.5m9 0H14M4.76 12.74l1.06-1.06m6.36-6.36l1.06-1.06" stroke="currentColor" stroke-width="1.2" stroke-linecap="round"/></svg>
        } @else {
          <svg width="16" height="16" viewBox="0 0 16 16"><path d="M13.5 8.5a6 6 0 01-6 6 6 6 0 110-12 4.5 4.5 0 006 6z" fill="currentColor"/></svg>
        }
      </button>

      <div class="avatar" (click)="auth.logout()" [title]="auth.username() + ' — Sair'">
        {{ (auth.username() ?? 'U').charAt(0).toUpperCase() }}
      </div>
    </header>

    <!-- backdrop mobile -->
    @if (sidebarOpen()) {
      <div class="backdrop" (click)="sidebarOpen.set(false)"></div>
    }

    <!-- sidebar -->
    <nav class="sidebar" [class.open]="sidebarOpen()">
      <div>
        <div class="nav-section-label">Principal</div>
        <div class="nav-list">
          @for (item of mainNav; track item.path) {
            <a [routerLink]="item.path" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: item.path === '/' }"
               class="nav-item" (click)="sidebarOpen.set(false)">
              <svg class="nav-icon" viewBox="0 0 18 18" [innerHTML]="item.svgContent"></svg>
              <span>{{ item.label }}</span>
              @if (item.path === '/approvals' && pending() > 0) {
                <span class="nav-badge">{{ pending() }}</span>
              }
            </a>
          }
        </div>
      </div>
      <div>
        <div class="nav-section-label">Configuração</div>
        <div class="nav-list">
          @for (item of configNav; track item.path) {
            <a [routerLink]="item.path" routerLinkActive="active"
               class="nav-item" (click)="sidebarOpen.set(false)">
              <svg class="nav-icon" viewBox="0 0 18 18" [innerHTML]="item.svgContent"></svg>
              <span>{{ item.label }}</span>
            </a>
          }
        </div>
      </div>
      <div class="sidebar-footer">
        <span class="env-dot on"></span>
        v1.0 · AgentForge
      </div>
    </nav>

    <!-- main content -->
    <main class="content">
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
    /* ── shell grid layout (artifact exact) ── */
    :host {
      display: grid;
      grid-template-columns: 240px 1fr;
      grid-template-rows: 56px 1fr;
      height: 100vh;
      overflow: hidden;
    }

    /* ── topbar ── */
    .topbar {
      grid-column: 1 / -1;
      background: var(--bg-1);
      border-bottom: 1px solid var(--border);
      display: flex;
      align-items: center;
      padding-inline: 20px;
      gap: 12px;
      z-index: 10;
    }
    .brand {
      display: flex;
      align-items: center;
      gap: 10px;
      font-weight: 700;
      font-size: 15px;
      letter-spacing: -.02em;
      min-width: 200px;
      cursor: pointer;
      text-decoration: none;
      color: var(--text-0);
    }
    .brand-icon {
      width: 30px; height: 30px;
      background: linear-gradient(135deg, var(--accent), #a78bfa);
      border-radius: 8px;
      display: grid; place-items: center;
      box-shadow: 0 2px 8px rgba(139,92,246,.3);
      flex-shrink: 0;
    }
    .topbar-spacer { flex: 1; }

    .env-pill {
      display: flex; align-items: center; gap: 6px;
      font-size: 12px; color: var(--text-1);
      background: var(--bg-2);
      padding: 4px 10px; border-radius: 20px;
      border: 1px solid var(--border);
    }
    .env-dot {
      width: 7px; height: 7px; border-radius: 50%;
      background: var(--danger);
    }
    .env-dot.on {
      background: var(--success);
      box-shadow: 0 0 6px var(--success);
    }

    .topbar-btn {
      background: none;
      border: 1px solid var(--border);
      color: var(--text-1);
      border-radius: var(--radius-sm);
      padding: 6px 8px;
      cursor: pointer;
      display: grid; place-items: center;
      transition: var(--transition);
    }
    .topbar-btn:hover {
      background: var(--bg-hover);
      color: var(--text-0);
      border-color: var(--border-active);
    }

    .avatar {
      width: 32px; height: 32px; border-radius: 50%;
      background: linear-gradient(135deg, var(--accent), #c084fc);
      display: grid; place-items: center;
      color: #fff; font-size: 13px; font-weight: 600;
      cursor: pointer;
    }

    .topbar-banner {
      display: inline-flex; align-items: center;
      padding: 2px 8px; border-radius: 999px;
      font-size: 11px; font-weight: 600;
    }
    .topbar-banner--warn { background: var(--warning-bg); color: var(--warning); }
    .topbar-banner--limit { background: rgba(139,92,246,.12); color: #d8b4fe; }

    .sidebar-toggle {
      display: none;
      background: none; border: none;
      color: var(--text-1); cursor: pointer;
      padding: 4px;
    }

    /* ── sidebar ── */
    .sidebar {
      background: var(--bg-1);
      border-right: 1px solid var(--border);
      padding: 16px 12px;
      overflow-y: auto;
      display: flex;
      flex-direction: column;
      gap: 24px;
    }

    .nav-section-label {
      font-size: 10px; font-weight: 600;
      letter-spacing: .08em; text-transform: uppercase;
      color: var(--text-2);
      padding: 0 10px; margin-bottom: 4px;
    }
    .nav-list { display: flex; flex-direction: column; gap: 2px; }

    .nav-item {
      display: flex; align-items: center; gap: 10px;
      padding: 8px 10px;
      border-radius: var(--radius-sm);
      color: var(--text-1);
      font-size: 13.5px; font-weight: 500;
      cursor: pointer;
      transition: var(--transition);
      position: relative;
      text-decoration: none;
      user-select: none;
    }
    .nav-item:hover { background: var(--bg-hover); color: var(--text-0); }
    .nav-item.active { background: var(--accent-dim); color: var(--accent); }
    .nav-item.active::before {
      content: '';
      position: absolute;
      left: 0; top: 6px; bottom: 6px;
      width: 3px;
      border-radius: 0 3px 3px 0;
      background: var(--accent);
    }

    .nav-icon { width: 18px; height: 18px; opacity: .7; flex-shrink: 0; }
    .nav-item.active .nav-icon { opacity: 1; }

    .nav-badge {
      margin-left: auto;
      background: var(--danger);
      color: #fff;
      font-size: 10px; font-weight: 700;
      padding: 1px 6px; border-radius: 10px;
      min-width: 18px; text-align: center;
    }

    .sidebar-footer {
      margin-top: auto;
      padding: 12px 10px;
      border-top: 1px solid var(--border);
      display: flex; align-items: center; gap: 8px;
      font-size: 12px; color: var(--text-2);
    }
    .sidebar-footer .env-dot { width: 6px; height: 6px; }

    /* ── backdrop (mobile) ── */
    .backdrop {
      position: fixed;
      inset: 0;
      z-index: 140;
      background: rgba(0,0,0,.5);
    }

    /* ── main content ── */
    .content {
      overflow-y: auto;
      padding: 24px;
      background: var(--bg-0);
    }

    /* ── banners ── */
    .banner {
      display: flex; gap: .75rem; align-items: center; justify-content: center;
      flex-wrap: wrap; padding: .5rem 1rem; font-size: .85rem;
      margin: -24px -24px 24px;
    }
    .banner--warn { background: var(--warning-bg); color: var(--warning); border-bottom: 1px solid var(--warning-border); }
    .banner--limit { background: var(--accent-dim); color: var(--accent); border-bottom: 1px solid var(--border); }
    .banner-btn { font-size: .8rem !important; }

    /* ── responsive ── */
    @media (max-width: 900px) {
      :host { grid-template-columns: 1fr; }
      .sidebar {
        display: none;
        position: fixed;
        top: 56px; left: 0; bottom: 0;
        width: 240px;
        z-index: 150;
      }
      .sidebar.open { display: flex; }
      .sidebar-toggle { display: grid; place-items: center; }
      .brand { min-width: unset; }
    }

    @media (pointer: coarse) {
      .nav-item { min-height: 44px; padding: 10px; }
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

  private readonly sanitizer = inject(DomSanitizer);
  private svg(html: string): SafeHtml { return this.sanitizer.bypassSecurityTrustHtml(html); }

  readonly mainNav: NavItem[] = [
    { path: '/', label: 'Projetos', group: 'main', svgContent: this.svg('<path d="M2 4.5A1.5 1.5 0 013.5 3h3.59a1.5 1.5 0 011.06.44l1.41 1.41a1.5 1.5 0 001.06.44H14.5A1.5 1.5 0 0116 6.79V13.5a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 012 13.5V4.5z" fill="currentColor"/>') },
    { path: '/approvals', label: 'Aprovações', group: 'main', svgContent: this.svg('<path d="M9 1.5a7.5 7.5 0 100 15 7.5 7.5 0 000-15zM8 5h2v4.59l2.7 2.71-1.4 1.41L8 10.41V5z" fill="currentColor"/>') },
    { path: '/live', label: 'Ao vivo', group: 'main', svgContent: this.svg('<circle cx="5" cy="9" r="2" fill="currentColor"/><circle cx="13" cy="9" r="2" fill="currentColor"/><circle cx="9" cy="4" r="2" fill="currentColor" opacity=".5"/><circle cx="9" cy="14" r="2" fill="currentColor" opacity=".5"/>') },
    { path: '/chat', label: 'Chat', group: 'main', svgContent: this.svg('<path d="M3 3.5A1.5 1.5 0 014.5 2h9A1.5 1.5 0 0115 3.5v8a1.5 1.5 0 01-1.5 1.5H6l-3 3V3.5z" fill="currentColor"/>') },
  ];
  readonly configNav: NavItem[] = [
    { path: '/agents', label: 'Agentes', group: 'config', svgContent: this.svg('<path d="M9 2a2.5 2.5 0 00-2.5 2.5V6h5V4.5A2.5 2.5 0 009 2zM5 6V4.5a4 4 0 118 0V6h1.5A1.5 1.5 0 0116 7.5v7a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 012 14.5v-7A1.5 1.5 0 013.5 6H5z" fill="currentColor"/>') },
    { path: '/prompts', label: 'Prompts', group: 'config', svgContent: this.svg('<path d="M9 1.5l1.3 4h4.2l-3.4 2.5 1.3 4L9 9.5 5.6 12l1.3-4L3.5 5.5h4.2L9 1.5z" fill="currentColor"/>') },
    { path: '/environments', label: 'Ambientes', group: 'config', svgContent: this.svg('<rect x="2" y="3" width="14" height="12" rx="2" fill="currentColor"/><path d="M5.5 7.5l2.5 2-2.5 2" stroke="var(--bg-1)" stroke-width="1.3" fill="none" stroke-linecap="round" stroke-linejoin="round"/><path d="M9.5 11.5h3" stroke="var(--bg-1)" stroke-width="1.3" fill="none" stroke-linecap="round"/>') },
    { path: '/profiles', label: 'Perfis', group: 'config', svgContent: this.svg('<path d="M14.5 9A5.5 5.5 0 119 3.5V1L12 4 9 7V4.5A4 4 0 1013 9h1.5z" fill="currentColor"/>') },
    { path: '/git', label: 'Git', group: 'config', svgContent: this.svg('<path d="M3 3h4v2H5v8h2v2H3V3zm8 0h4v12h-4v-2h2V5h-2V3z" fill="currentColor" opacity=".8"/><circle cx="9" cy="9" r="1.5" fill="currentColor"/>') },
    { path: '/integration', label: 'Integração', group: 'config', svgContent: this.svg('<path d="M9 2v5m0 0L6.5 4.5M9 7l2.5-2.5M4 9h10M9 11v5m0 0l2.5-2.5M9 16L6.5 13.5" stroke="currentColor" stroke-width="1.5" fill="none" stroke-linecap="round"/>') },
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
