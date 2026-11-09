import { Component, ElementRef, effect, inject, signal, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { distinctUntilChanged, interval, map } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ChatDetail, ChatMessage, ChatSession, LiveAgent, Project } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { BadgeComponent } from '../shared/shared';
import { MarkdownComponent } from '../shared/markdown.component';

@Component({
  selector: 'app-chat-page',
  standalone: true,
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, BadgeComponent, MarkdownComponent],
  template: `
    <div class="page layout">
      <aside class="side">
        <div class="row">
          <h1>Chat</h1>
          <span class="spacer"></span>
          <button mat-flat-button type="button" (click)="newOpen.set(!newOpen())" data-testid="new-chat">Nova conversa</button>
        </div>

        @if (newOpen()) {
          <section class="card" data-testid="new-form">
            <mat-form-field appearance="outline">
              <mat-label>Agente</mat-label>
              <mat-select [ngModel]="agentKey()" (ngModelChange)="agentKey.set($event)" data-testid="new-agent">
                @for (a of agents(); track key(a)) { <mat-option [value]="key(a)">{{ a.displayName }} · {{ a.environment }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Projeto</mat-label>
              <mat-select [ngModel]="projectId()" (ngModelChange)="projectId.set($event)" data-testid="new-project">
                @for (p of projects(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
              </mat-select>
            </mat-form-field>
            @if (agents().length === 0) { <p class="muted small">Nenhum agente conectado e acessível. Veja a página Ao vivo.</p> }
            <div class="row">
              <button mat-flat-button type="button" [disabled]="!agentKey() || !projectId() || creating()" (click)="create()" data-testid="new-create">Abrir</button>
              <button mat-button type="button" (click)="newOpen.set(false)">Cancelar</button>
            </div>
          </section>
        }

        @for (s of sessions(); track s.id) {
          <a class="session" [class.active]="s.id === currentId()" [routerLink]="['/chat', s.id]" data-testid="session">
            <div class="title">{{ s.title }}</div>
            <div class="muted small">{{ s.project }} · {{ s.environment }}</div>
          </a>
        }
        @if (sessions().length === 0) { <p class="muted small" data-testid="no-sessions">Nenhuma conversa ainda.</p> }
      </aside>

      <main class="conversation">
        @if (detail(); as d) {
          <div class="row head">
            <div>
              <strong>{{ d.session.title }}</strong>
              <div class="muted small">{{ d.session.project }} · {{ d.session.agent }} em {{ d.session.environment }}</div>
            </div>
            <span class="spacer"></span>
            <button mat-button type="button" [disabled]="d.session.busy" (click)="remove()">Apagar</button>
          </div>

          <div class="messages" #scroller data-testid="messages">
            @if (d.messages.length === 0) {
              <p class="muted">
                Converse sobre o projeto. Quando fizer sentido, peça <em>"proponha uma demanda para..."</em>: o agente sugere e você cria com um clique.
              </p>
            }
            @for (m of d.messages; track m.id) {
              <div class="msg" [class.user]="m.role === 'User'" [class.failed]="m.failed" data-testid="message">
                @if (m.role === 'User') {
                  <div class="bubble user-bubble">{{ m.text }}</div>
                } @else {
                  <div class="bubble"><app-markdown [source]="m.text" /></div>
                  @for (p of m.proposals; track $index; let i = $index) {
                    <div class="proposal" data-testid="proposal">
                      <div class="row"><app-badge label="demanda proposta" tone="info" /><strong>{{ p.title }}</strong></div>
                      @if (p.description) { <p class="muted small">{{ p.description }}</p> }
                      @if (p.demandId) {
                        <a [routerLink]="['/demands', p.demandId]" data-testid="proposal-created">Demanda criada — abrir →</a>
                      } @else {
                        <div class="row">
                          <button mat-stroked-button type="button" [disabled]="acting()" (click)="createDemand(m, i, false)" data-testid="proposal-create">Criar demanda</button>
                          <button mat-flat-button type="button" [disabled]="acting()" (click)="createDemand(m, i, true)" data-testid="proposal-start">Criar e iniciar refinamento</button>
                        </div>
                      }
                    </div>
                  }
                }
              </div>
            }
            @if (d.session.busy) { <p class="muted" data-testid="busy">O agente está respondendo…</p> }
            @else if (lastIsUser(d)) { <p class="muted small" data-testid="no-reply">Sem resposta a esta mensagem (a API reiniciou?). Envie de novo.</p> }
          </div>

          <form class="composer" (ngSubmit)="send()">
            <mat-form-field appearance="outline" class="grow">
              <mat-label>Mensagem</mat-label>
              <textarea matInput rows="2" name="text" [ngModel]="text()" (ngModelChange)="text.set($event)" [disabled]="d.session.busy"
                (keydown.control.enter)="send()" (keydown.meta.enter)="send()" data-testid="composer"></textarea>
            </mat-form-field>
            <button mat-flat-button type="submit" [disabled]="!text().trim() || d.session.busy || sending()" data-testid="send">Enviar</button>
          </form>
        } @else if (currentId()) {
          <p class="muted">Carregando conversa…</p>
        } @else {
          <section class="card empty" data-testid="pick">
            <strong>Escolha uma conversa ou abra uma nova.</strong>
            <span class="muted">Dá para conversar com o agente de qualquer máquina, com o contexto de um projeto, e pedir que ele proponha demandas.</span>
          </section>
        }
      </main>
    </div>
  `,
  styles: `
    .layout { display: grid; grid-template-columns: 320px 1fr; gap: 1.25rem; align-items: start; }
    .side { display: flex; flex-direction: column; gap: .5rem; }
    .side .card { display: flex; flex-direction: column; padding: .75rem; background: #151515; border: 1px solid #2a2a2a; border-radius: 10px; }
    .session { display: block; padding: .5rem .7rem; border: 1px solid #2a2a2a; border-radius: 8px; color: inherit; text-decoration: none; background: #151515; }
    .session:hover, .session.active { border-color: #8ab4f8; }
    .session .title { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font-weight: 600; }
    .conversation { display: flex; flex-direction: column; min-height: 70vh; border: 1px solid #2a2a2a; border-radius: 10px; background: #111; }
    .head { padding: .75rem 1rem; border-bottom: 1px solid #2a2a2a; }
    .messages { flex: 1; overflow-y: auto; padding: 1rem; max-height: 62vh; display: flex; flex-direction: column; gap: .75rem; }
    .msg { display: flex; flex-direction: column; gap: .4rem; max-width: 85%; }
    .msg.user { align-self: flex-end; align-items: flex-end; }
    .bubble { padding: .6rem .85rem; border-radius: 12px; background: #1b1b1b; border: 1px solid #2a2a2a; }
    .user-bubble { background: #1f3a5c; border-color: #2b4f7a; white-space: pre-wrap; }
    .msg.failed .bubble { border-color: #5c1f1f; background: #231414; }
    .proposal { padding: .6rem .8rem; border: 1px dashed #4a6fa5; border-radius: 10px; background: #12202f; }
    .proposal p { margin: .3rem 0 .5rem; }
    .composer { display: flex; gap: .75rem; align-items: flex-start; padding: .75rem 1rem; border-top: 1px solid #2a2a2a; }
    .grow { flex: 1; }
    .empty { margin: 1rem; padding: 1rem; display: flex; flex-direction: column; gap: .3rem; }
    @media (max-width: 900px) { .layout { grid-template-columns: 1fr; } }
  `,
})
export class ChatPage {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);
  private readonly realtime = inject(RealtimeService);
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');

  readonly sessions = signal<ChatSession[]>([]);
  readonly detail = signal<ChatDetail | null>(null);
  readonly currentId = signal<string | null>(null);
  readonly text = signal('');
  readonly sending = signal(false);
  readonly acting = signal(false);
  readonly newOpen = signal(false);
  readonly creating = signal(false);
  readonly agents = signal<LiveAgent[]>([]);
  readonly projects = signal<Project[]>([]);
  readonly agentKey = signal<string | null>(null);
  readonly projectId = signal<string | null>(null);

  constructor() {
    this.loadSessions();
    this.api.projects().subscribe({ next: (p) => this.projects.set(p), error: () => undefined });
    this.api.liveAgents().subscribe({
      next: (d) => {
        // só agentes que dá para usar: máquina no ar, instalado e conectado
        const usable = d.agents.filter((a) => a.reachable && a.installed && a.authenticated);
        this.agents.set(usable);
        if (usable.length > 0) this.agentKey.set(this.key(usable[0]));
      },
      error: () => undefined,
    });

    this.route.paramMap.pipe(map((p) => p.get('id')), distinctUntilChanged(), takeUntilDestroyed()).subscribe((id) => {
      this.currentId.set(id);
      this.detail.set(null);
      if (id) this.loadDetail(id);
    });

    // entra no grupo da conversa aberta (e sai ao trocar/fechar): a resposta do agente chega por SignalR
    effect((onCleanup) => {
      const id = this.currentId();
      if (!id) return;
      untracked(() => void this.realtime.joinChat(id));
      onCleanup(() => untracked(() => void this.realtime.leaveChat(id)));
    });

    this.realtime.chatChanged$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.sessionId === this.currentId()) this.loadDetail(e.sessionId);
      this.loadSessions();
    });
    this.realtime.reconnected$.pipe(takeUntilDestroyed()).subscribe(() => {
      const id = this.currentId();
      if (id) this.loadDetail(id);
    });
    // sem tempo real, a resposta ainda chega: recarrega enquanto o agente responde
    interval(4000).pipe(takeUntilDestroyed()).subscribe(() => {
      const id = this.currentId();
      if (id && this.detail()?.session.busy) this.loadDetail(id);
    });

    effect(() => {
      this.detail();
      queueMicrotask(() => {
        const el = this.scroller()?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
      });
    });
  }

  key(a: LiveAgent): string {
    return `${a.environmentId ?? 'default'}|${a.agent}`;
  }

  lastIsUser(d: ChatDetail): boolean {
    return d.messages.length > 0 && d.messages[d.messages.length - 1].role === 'User';
  }

  loadSessions(): void {
    this.api.chats().subscribe({ next: (s) => this.sessions.set(s), error: () => undefined });
  }

  loadDetail(id: string): void {
    this.api.chat(id).subscribe({
      next: (d) => {
        if (this.currentId() === id) this.detail.set(d);
      },
      error: () => {
        if (this.currentId() === id) {
          this.snack.open('Conversa não encontrada.', 'OK', { duration: 4000 });
          void this.router.navigate(['/chat']);
        }
      },
    });
  }

  create(): void {
    const agent = this.agents().find((a) => this.key(a) === this.agentKey());
    const projectId = this.projectId();
    if (!agent || !projectId) return;
    this.creating.set(true);
    this.api.createChat({ projectId, environmentId: agent.environmentId, adapterName: agent.agent }).subscribe({
      next: (s) => {
        this.creating.set(false);
        this.newOpen.set(false);
        this.loadSessions();
        void this.router.navigate(['/chat', s.id]);
      },
      error: () => {
        this.creating.set(false);
        this.snack.open('Não foi possível abrir a conversa.', 'OK', { duration: 5000 });
      },
    });
  }

  send(): void {
    const id = this.currentId();
    const text = this.text().trim();
    if (!id || !text || this.sending() || this.detail()?.session.busy) return;
    this.sending.set(true);
    this.api.sendChat(id, text).subscribe({
      next: () => {
        this.sending.set(false);
        this.text.set('');
        this.loadDetail(id);
        this.loadSessions();
      },
      error: (err) => {
        this.sending.set(false);
        this.snack.open(err?.error?.error ?? err?.error?.errors?.text?.[0] ?? 'Não foi possível enviar.', 'OK', { duration: 6000 });
        this.loadDetail(id);
      },
    });
  }

  createDemand(m: ChatMessage, index: number, start: boolean): void {
    const id = this.currentId();
    if (!id) return;
    this.acting.set(true);
    this.api.createProposalDemand(id, m.id, index, start).subscribe({
      next: (r) => {
        this.acting.set(false);
        this.snack.open(r.started ? 'Demanda criada e refinamento iniciado.' : 'Demanda criada.', 'OK', { duration: 4000 });
        this.loadDetail(id);
      },
      error: (err) => {
        this.acting.set(false);
        this.snack.open(err?.error?.error ?? 'Não foi possível criar a demanda.', 'OK', { duration: 6000 });
        this.loadDetail(id);
      },
    });
  }

  remove(): void {
    const id = this.currentId();
    if (!id || !confirm('Apagar esta conversa? As demandas já criadas continuam.')) return;
    this.api.deleteChat(id).subscribe({
      next: () => {
        this.loadSessions();
        void this.router.navigate(['/chat']);
      },
      error: (err) => this.snack.open(err?.error?.error ?? 'Não foi possível apagar.', 'OK', { duration: 5000 }),
    });
  }
}
