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
    <div class="page">
      <div class="row">
        <h1>Agentes ao vivo</h1>
        <app-badge [label]="realtime.status() === 'connected' ? 'tempo real' : 'sem tempo real (atualizando a cada 10 s)'"
          [tone]="realtime.status() === 'connected' ? 'ok' : 'warn'" />
        <span class="spacer"></span>
        <button mat-button type="button" (click)="load()">Atualizar</button>
      </div>
      <p class="muted">
        Os agentes de todas as máquinas, o que cada um está fazendo agora e um atalho para <strong>conversar</strong> com um deles
        (tirar dúvidas, refinar ideias e criar novas demandas).
      </p>

      @if (data(); as d) {
        <h2>Agentes conectados</h2>
        <div class="grid" data-testid="agents">
          @for (a of d.agents; track a.environment + a.agent) {
            <section class="card agent" [attr.data-state]="stateOf(a)" data-testid="agent-card">
              <div class="row">
                <strong>{{ a.displayName }}</strong>
                <app-badge [label]="label(a)" [tone]="tone(a)" />
              </div>
              <div class="muted small">{{ a.environment }} · {{ a.mode === 'host' ? 'host agent' : 'container da API' }}@if (a.version) { · v{{ a.version }} }</div>
              @if (a.runningNow > 0) {
                <div class="small" data-testid="running-now">{{ a.runningNow }} execução(ões) agora</div>
              }
              @if (a.usageLimit; as l) {
                <div class="small warnbox" data-testid="limit">{{ limitText(a) }}</div>
              } @else if (stateOf(a) !== 'ready' && stateOf(a) !== 'busy' && a.detail) {
                <div class="small muted">{{ a.detail }}</div>
              }
              <div class="row actions">
                <button mat-stroked-button type="button" [disabled]="!canChat(a)" (click)="startChat(a)" data-testid="chat-button">Conversar</button>
              </div>
            </section>
          }
        </div>

        @if (chatTarget(); as t) {
          <section class="card start" data-testid="chat-start">
            <strong>Conversar com {{ t.displayName }} em {{ t.environment }}</strong>
            <div class="row">
              <mat-form-field appearance="outline" class="field">
                <mat-label>Projeto</mat-label>
                <mat-select [ngModel]="projectId()" (ngModelChange)="projectId.set($event)" data-testid="chat-project">
                  @for (p of projects(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
                </mat-select>
              </mat-form-field>
              <button mat-flat-button type="button" [disabled]="!projectId() || creating()" (click)="openChat()" data-testid="chat-open">Abrir conversa</button>
              <button mat-button type="button" (click)="chatTarget.set(null)">Cancelar</button>
            </div>
            @if (projects().length === 0) { <p class="muted small">Cadastre um projeto antes de conversar.</p> }
          </section>
        }

        <h2>Executando agora <span class="muted small">({{ d.running.length }})</span></h2>
        @if (d.running.length === 0) {
          <section class="card empty" data-testid="running-empty">
            <span class="muted">Nenhum agente trabalhando neste momento. Quando uma tarefa andar, ela aparece aqui na hora.</span>
          </section>
        }
        @for (r of d.running; track r.runId) {
          <section class="card run" data-testid="run-row">
            <div class="row">
              <app-badge [label]="roleLabel(r.role)" tone="info" />
              <strong>{{ r.task ?? 'Refinamento da demanda' }}</strong>
              <span class="muted small">{{ r.project }} · {{ r.environment }}</span>
              <span class="spacer"></span>
              <span class="mono small">{{ since(r) }}</span>
              @if (r.workItemId) { <a mat-button [routerLink]="['/tasks', r.workItemId]">Abrir tarefa</a> }
            </div>
            @if (lastLine(r.runId); as line) { <div class="mono small tail" data-testid="run-tail">{{ line }}</div> }
          </section>
        }
        <p class="muted small">Atualizado às {{ whenLabel(d.at) }}.</p>
      } @else if (error()) {
        <p class="alert bad">Não foi possível carregar os agentes.</p>
      } @else {
        <p class="muted">Carregando…</p>
      }
    </div>
  `,
  styles: `
    .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(300px, 1fr)); gap: 1rem; }
    .card { padding: 1rem; background: #151515; border: 1px solid #2a2a2a; border-radius: 10px; margin-top: .5rem; }
    .agent { display: flex; flex-direction: column; gap: .35rem; margin-top: 0; }
    .agent[data-state='ready'] { border-color: #1f4d2b; }
    .agent[data-state='limited'], .agent[data-state='not-connected'] { border-color: #5a4a14; }
    .agent[data-state='unreachable'], .agent[data-state='not-installed'] { border-color: #5c1f1f; }
    .warnbox { padding: .35rem .6rem; border-left: 3px solid #c9a227; background: #1d1a10; border-radius: 4px; }
    .actions { margin-top: .4rem; }
    .start { margin-top: 1rem; }
    .field { min-width: 18rem; }
    .run .tail { margin-top: .4rem; color: #aaa; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .empty { margin-top: .5rem; }
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
