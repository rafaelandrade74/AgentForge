import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { debounceTime, interval } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ROLE_LABEL } from '../core/labels';
import { AgentRole, LiveAgent, LiveAgents, LiveRun, Project, RunEvent } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { usageLimitText, whenLabel } from '../core/usage-limit';
import { BadgeComponent } from '../shared/shared';

export type AgentState = 'unreachable' | 'not-installed' | 'not-connected' | 'limited' | 'busy' | 'ready';

/** O estado que a pessoa precisa ver, do mais grave ao normal. */
export function agentState(a: LiveAgent): AgentState {
  if (!a.reachable) return 'unreachable';
  if (!a.installed) return 'not-installed';
  if (!a.authenticated) return 'not-connected';
  if (a.usageLimit) return 'limited';
  return a.runningNow > 0 ? 'busy' : 'ready';
}

export const STATE_LABEL: Record<AgentState, string> = {
  unreachable: 'máquina inacessível',
  'not-installed': 'não instalado',
  'not-connected': 'não conectado',
  limited: 'limite de uso',
  busy: 'trabalhando',
  ready: 'pronto',
};

const STATE_TONE: Record<AgentState, 'ok' | 'warn' | 'bad' | 'info' | 'idle'> = {
  unreachable: 'bad',
  'not-installed': 'bad',
  'not-connected': 'warn',
  limited: 'warn',
  busy: 'info',
  ready: 'ok',
};

/** "2 min 5 s" desde o início da execução. */
export function elapsed(startedAt: string, now: number): string {
  const s = Math.max(0, Math.round((now - new Date(startedAt).getTime()) / 1000));
  return s < 60 ? `${s} s` : `${Math.floor(s / 60)} min ${s % 60} s`;
}

@Component({
  selector: 'app-live-page',
  standalone: true,
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatSelectModule, BadgeComponent],
  template: `
    <div class="content-area">
      <div class="page-header">
        <div>
          <div class="page-title">Agentes ao vivo</div>
          <div class="page-subtitle">Status em tempo real de todos os agentes</div>
        </div>
        <div class="header-actions">
          <span class="badge" [class]="realtime.status() === 'connected' ? 'badge-ok' : 'badge-warn'" style="padding:4px 10px">{{ realtime.status() === 'connected' ? '● Conectado' : '● Desconectado' }}</span>
          <button class="btn btn-secondary" type="button" (click)="load()">Atualizar</button>
        </div>
      </div>

      @if (data(); as d) {
        <div class="grid-auto" style="grid-template-columns:repeat(auto-fill,minmax(280px,1fr))" data-testid="agents">
          @for (a of d.agents; track a.environment + a.agent) {
            <div class="card card-p agent-live-card" [attr.data-state]="stateOf(a)" data-testid="agent-card">
              <div style="display:flex;align-items:center;gap:8px;margin-bottom:10px">
                <div class="agent-avatar">{{ a.displayName?.charAt(0) ?? 'A' }}</div>
                <div>
                  <div style="font-size:13px;font-weight:600">{{ a.displayName }}</div>
                  <div style="font-size:11px;color:var(--text-2)">{{ a.environment }} · {{ a.mode === 'host' ? 'host agent' : 'container' }}@if (a.version) { · v{{ a.version }} }</div>
                </div>
              </div>
              <div style="display:flex;gap:6px;margin-bottom:10px">
                <span class="badge" [class]="'badge-' + tone(a)">{{ label(a) }}</span>
                @if (a.runningNow > 0) { <span class="badge badge-idle">{{ a.runningNow }} rodando</span> }
              </div>
              @if (a.usageLimit) {
                <div style="font-size:11px;color:var(--warning);margin-bottom:8px" data-testid="limit">⚠ {{ limitText(a) }}</div>
              } @else if (stateOf(a) !== 'ready' && stateOf(a) !== 'busy' && a.detail) {
                <div style="font-size:12px;color:var(--text-2);margin-bottom:8px">{{ a.detail }}</div>
              }
              <div style="display:flex;gap:6px">
                <button class="btn btn-secondary btn-sm" type="button" [disabled]="!canChat(a)" (click)="startChat(a)" data-testid="chat-button">Conversar</button>
              </div>
            </div>
          }
        </div>

        @if (chatTarget(); as t) {
          <div class="card card-p" style="margin-top:16px;display:flex;flex-direction:column;gap:12px" data-testid="chat-start">
            <strong>Conversar com {{ t.displayName }} em {{ t.environment }}</strong>
            <div style="display:flex;gap:8px;align-items:center;flex-wrap:wrap">
              <mat-form-field appearance="outline" style="min-width:18rem;max-width:100%">
                <mat-label>Projeto</mat-label>
                <mat-select [ngModel]="projectId()" (ngModelChange)="projectId.set($event)" data-testid="chat-project">
                  @for (p of projects(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
                </mat-select>
              </mat-form-field>
              <button class="btn btn-primary" type="button" [disabled]="!projectId() || creating()" (click)="openChat()" data-testid="chat-open">Abrir conversa</button>
              <button class="btn btn-secondary" type="button" (click)="chatTarget.set(null)">Cancelar</button>
            </div>
            @if (projects().length === 0) { <p style="font-size:12px;color:var(--text-2)">Cadastre um projeto antes de conversar.</p> }
          </div>
        }

        <div class="section-title" style="margin-top:16px">Execuções em andamento <span class="count">{{ d.running.length }}</span></div>
        @if (d.running.length === 0) {
          <div class="card card-p" style="text-align:center;color:var(--text-2)" data-testid="running-empty">
            Nenhum agente trabalhando neste momento.
          </div>
        }
        @for (r of d.running; track r.runId) {
          <div class="card card-p" style="margin-bottom:8px" data-testid="run-row">
            <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
              <span class="a-dot running" style="margin:0"></span>
              <span class="badge badge-accent">{{ roleLabel(r.role) }}</span>
              <div>
                @if (r.workItemId) {
                  <a [routerLink]="['/tasks', r.workItemId]" style="font-size:13px;font-weight:500;color:var(--accent);text-decoration:none" data-testid="run-task-link">{{ r.task ?? 'Refinamento da demanda' }}</a>
                } @else {
                  <div style="font-size:13px;font-weight:500">{{ r.task ?? 'Refinamento da demanda' }}</div>
                }
                <div style="font-size:11px;color:var(--text-2)">{{ r.project }} · {{ r.environment }} · {{ since(r) }}</div>
              </div>
              <div style="margin-left:auto;font-family:var(--mono);font-size:11px;color:var(--text-2)">
                @if (lastLine(r.runId); as line) { ▸ {{ line }} }
              </div>
            </div>
          </div>
        }
        <p style="font-size:12px;color:var(--text-2)">Atualizado às {{ whenLabel(d.at) }}.</p>
      } @else if (error()) {
        <p style="color:var(--danger)">Não foi possível carregar os agentes.</p>
      } @else {
        <p style="color:var(--text-2)">Carregando…</p>
      }
    </div>
  `,
  styles: `
    .content-area { padding: 24px; max-width: 1200px; }
    .agent-live-card { border-top: 3px solid var(--border); }
    .agent-live-card:hover { border-color: var(--border-active); }
    .agent-live-card[data-state='ready'] { border-top-color: var(--success); }
    .agent-live-card[data-state='busy'] { border-top-color: var(--info); }
    .agent-live-card[data-state='limited'], .agent-live-card[data-state='not-connected'] { border-top-color: var(--warning); }
    .agent-live-card[data-state='unreachable'], .agent-live-card[data-state='not-installed'] { border-top-color: var(--danger); }
    .a-dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
    .a-dot.running { background: var(--accent); box-shadow: 0 0 8px rgba(139,92,246,.25); animation: pulse 2s infinite; }
    @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: .5; } }
    @media (max-width: 639px) { .content-area { padding: 16px; } }
  `,
})
export class LivePage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  readonly realtime = inject(RealtimeService);

  readonly data = signal<LiveAgents | null>(null);
  readonly error = signal(false);
  readonly projects = signal<Project[]>([]);
  readonly chatTarget = signal<LiveAgent | null>(null);
  readonly projectId = signal<string | null>(null);
  readonly creating = signal(false);
  private readonly now = signal(Date.now());
  /** Última linha de log de cada execução, vinda do tempo real. */
  private readonly lastEvents = signal<Record<string, string>>({});

  readonly whenLabel = whenLabel;
  readonly stateOf = agentState;
  readonly connected = computed(() => this.realtime.status() === 'connected');

  constructor() {
    this.load();
    this.api.projects().subscribe({ next: (p) => this.projects.set(p), error: () => undefined });

    void this.realtime.joinLive();
    inject(DestroyRef).onDestroy(() => void this.realtime.leaveLive());

    this.realtime.liveRunEvent$.pipe(takeUntilDestroyed()).subscribe((e) => this.onEvent(e));
    this.realtime.liveRunEvent$
      .pipe(debounceTime(400), takeUntilDestroyed())
      .subscribe((e) => {
        if (e.kind === 'run_started' || e.kind === 'run_finished') this.load();
      });
    this.realtime.reconnected$.pipe(takeUntilDestroyed()).subscribe(() => this.load());
    interval(10_000).pipe(takeUntilDestroyed()).subscribe(() => this.load());
    interval(1000).pipe(takeUntilDestroyed()).subscribe(() => this.now.set(Date.now()));
  }

  load(): void {
    this.api.liveAgents().subscribe({
      next: (d) => {
        this.data.set(d);
        this.error.set(false);
      },
      error: () => this.error.set(this.data() === null),
    });
  }

  private onEvent(e: RunEvent): void {
    if (e.kind === 'run_finished') {
      this.lastEvents.update((m) => {
        const { [e.runId]: _gone, ...rest } = m;
        return rest;
      });
      return;
    }
    if (['assistant', 'tool', 'tool_result', 'system', 'result', 'rate_limit'].includes(e.kind) && e.text) {
      this.lastEvents.update((m) => ({ ...m, [e.runId]: `${e.kind}: ${e.text.replace(/\s+/g, ' ').trim()}` }));
    }
  }

  lastLine(runId: string): string | null {
    return this.lastEvents()[runId] ?? null;
  }

  since(r: LiveRun): string {
    return elapsed(r.startedAt, this.now());
  }

  label(a: LiveAgent): string {
    return STATE_LABEL[agentState(a)];
  }

  tone(a: LiveAgent) {
    return STATE_TONE[agentState(a)];
  }

  limitText(a: LiveAgent): string {
    return a.usageLimit ? usageLimitText(a.usageLimit) : '';
  }

  roleLabel(role: string): string {
    return ROLE_LABEL[role as AgentRole] ?? role;
  }

  /** Dá para conversar: máquina no ar, CLI instalado e conectado. (Com limite de uso a conversa abre, mas avisa ao responder.) */
  canChat(a: LiveAgent): boolean {
    return a.reachable && a.installed && a.authenticated;
  }

  startChat(a: LiveAgent): void {
    this.chatTarget.set(a);
    if (!this.projectId() && this.projects().length > 0) this.projectId.set(this.projects()[0].id);
  }

  openChat(): void {
    const target = this.chatTarget();
    const projectId = this.projectId();
    if (!target || !projectId) return;
    this.creating.set(true);
    this.api.createChat({ projectId, environmentId: target.environmentId, adapterName: target.agent }).subscribe({
      next: (s) => {
        this.creating.set(false);
        void this.router.navigate(['/chat', s.id]);
      },
      error: (err) => {
        this.creating.set(false);
        this.snack.open(err?.error?.errors ? 'Não foi possível abrir a conversa.' : (err?.error?.error ?? 'Não foi possível abrir a conversa.'), 'OK', { duration: 6000 });
      },
    });
  }
}
