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
    <div class="chat-layout">
      <div class="chat-sidebar">
        <div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:8px">
          <div style="font-size:16px;font-weight:700">Chat</div>
          <button class="btn btn-primary btn-sm" type="button" (click)="newOpen.set(!newOpen())" data-testid="new-chat">Nova</button>
        </div>

        @if (newOpen()) {
          <div class="card card-p" style="margin-bottom:8px" data-testid="new-form">
            <mat-form-field appearance="outline" style="width:100%">
              <mat-label>Agente</mat-label>
              <mat-select [ngModel]="agentKey()" (ngModelChange)="agentKey.set($event)" data-testid="new-agent">
                @for (a of agents(); track key(a)) { <mat-option [value]="key(a)">{{ a.displayName }} · {{ a.environment }}</mat-option> }
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline" style="width:100%">
              <mat-label>Projeto</mat-label>
              <mat-select [ngModel]="projectId()" (ngModelChange)="projectId.set($event)" data-testid="new-project">
                @for (p of projects(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }
              </mat-select>
            </mat-form-field>
            @if (agents().length === 0) { <p style="font-size:12px;color:var(--text-2)">Nenhum agente conectado.</p> }
            <div style="display:flex;gap:6px">
              <button class="btn btn-primary btn-sm" type="button" [disabled]="!agentKey() || !projectId() || creating()" (click)="create()" data-testid="new-create">Abrir</button>
              <button class="btn btn-secondary btn-sm" type="button" (click)="newOpen.set(false)">Cancelar</button>
            </div>
          </div>
        }

        @for (s of sessions(); track s.id) {
          <a class="chat-session" [class.active]="s.id === currentId()" [routerLink]="['/chat', s.id]" data-testid="session">
            <div class="chat-session-title">{{ s.title }}</div>
            <div class="chat-session-meta">{{ s.project }} · {{ s.environment }}</div>
          </a>
        }
        @if (sessions().length === 0) { <p style="font-size:12px;color:var(--text-2)" data-testid="no-sessions">Nenhuma conversa ainda.</p> }
      </div>

      <div class="chat-main">
        @if (detail(); as d) {
          <div style="font-size:14px;font-weight:600;margin-bottom:4px">{{ d.session.title }}</div>
          <div style="font-size:12px;color:var(--text-2);margin-bottom:16px">{{ d.session.project }} · {{ d.session.agent }} em {{ d.session.environment }}
            <button class="btn btn-secondary btn-sm" type="button" [disabled]="d.session.busy" (click)="remove()" style="margin-left:8px">Apagar</button>
          </div>

          <div class="chat-messages" #scroller data-testid="messages">
            @if (d.messages.length === 0) {
              <p style="color:var(--text-2)">
                Converse sobre o projeto. Peça <em>"proponha uma demanda para..."</em> e o agente sugere.
              </p>
            }
            @for (m of d.messages; track m.id) {
              <div data-testid="message">
                @if (m.role === 'User') {
                  <div class="chat-bubble user">{{ m.text }}</div>
                } @else {
                  <div class="chat-bubble agent"><app-markdown [source]="m.text" /></div>
                  @for (p of m.proposals; track $index; let i = $index) {
                    <div class="proposal" data-testid="proposal">
                      <div style="font-size:11px;font-weight:600;color:var(--accent);margin-bottom:4px">💡 Proposta de demanda</div>
                      <div style="font-size:12px">{{ p.title }}</div>
                      @if (p.description) { <div style="font-size:11px;color:var(--text-2);margin-top:2px">{{ p.description }}</div> }
                      @if (p.demandId) {
                        <a [routerLink]="['/demands', p.demandId]" style="font-size:12px;margin-top:6px;display:inline-block" data-testid="proposal-created">Demanda criada — abrir →</a>
                      } @else {
                        <div style="margin-top:6px;display:flex;gap:6px">
                          <button class="btn btn-secondary btn-sm" type="button" [disabled]="acting()" (click)="createDemand(m, i, false)" data-testid="proposal-create">Criar demanda</button>
                          <button class="btn btn-primary btn-sm" type="button" [disabled]="acting()" (click)="createDemand(m, i, true)" data-testid="proposal-start">Criar e iniciar</button>
                        </div>
                      }
                    </div>
                  }
                }
              </div>
            }
            @if (d.session.busy) { <p style="color:var(--text-2)" data-testid="busy">O agente está respondendo…</p> }
            @else if (lastIsUser(d)) { <p style="font-size:12px;color:var(--text-2)" data-testid="no-reply">Sem resposta. Envie de novo.</p> }
          </div>

          <div class="chat-composer">
            <input class="form-input" style="flex:1" placeholder="Enviar mensagem... (Ctrl+Enter)" [value]="text()" (input)="text.set($any($event.target).value)" [disabled]="detail()?.session?.busy ?? false"
              (keydown.control.enter)="send()" (keydown.meta.enter)="send()" data-testid="composer">
            <button class="btn btn-primary" type="button" [disabled]="!text().trim() || (detail()?.session?.busy ?? false) || sending()" (click)="send()" data-testid="send">Enviar</button>
          </div>
        } @else if (currentId()) {
          <p style="color:var(--text-2);padding:20px">Carregando conversa…</p>
        } @else {
          <div style="display:flex;align-items:center;justify-content:center;height:100%;padding:40px;text-align:center;color:var(--text-2)" data-testid="pick">
            <div><strong style="color:var(--text-0)">Escolha uma conversa ou abra uma nova.</strong><br>Converse com o agente e peça que ele proponha demandas.</div>
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .chat-layout { display: grid; grid-template-columns: 300px 1fr; gap: 0; height: calc(100vh - 56px); overflow: hidden; }
    .chat-sidebar { background: var(--bg-1); border-right: 1px solid var(--border); padding: 16px; display: flex; flex-direction: column; gap: 4px; overflow-y: auto; }
    .chat-session { display: block; padding: 10px 12px; border-radius: var(--radius-sm); cursor: pointer; transition: var(--transition); text-decoration: none; color: inherit; }
    .chat-session:hover { background: var(--bg-hover); }
    .chat-session.active { background: var(--accent-dim); }
    .chat-session-title { font-size: 13px; font-weight: 500; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .chat-session-meta { font-size: 11px; color: var(--text-2); margin-top: 2px; }
    .chat-main { display: flex; flex-direction: column; padding: 20px; overflow: hidden; }
    .chat-messages { flex: 1; overflow-y: auto; display: flex; flex-direction: column; gap: 12px; padding-bottom: 16px; }
    .chat-bubble { max-width: 80%; padding: 10px 14px; border-radius: var(--radius); font-size: 13.5px; line-height: 1.5; }
    .chat-bubble.user { background: var(--accent); color: #fff; align-self: flex-end; border-bottom-right-radius: 4px; white-space: pre-wrap; }
    .chat-bubble.agent { background: var(--bg-2); border: 1px solid var(--border); align-self: flex-start; border-bottom-left-radius: 4px; }
    .chat-composer { display: flex; gap: 8px; padding-top: 12px; border-top: 1px solid var(--border); }
    .proposal { margin-top: 8px; padding: 8px 10px; background: var(--accent-dim); border-radius: var(--radius-sm); border: 1px solid var(--border-active); }
    @media (max-width: 900px) { .chat-layout { grid-template-columns: 1fr; } .chat-sidebar { display: none; } }
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
