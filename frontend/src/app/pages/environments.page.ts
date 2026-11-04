import { NgTemplateOutlet } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { RouterLink } from '@angular/router';
import { ApiService } from '../core/api.service';
import { EnvironmentInput, EnvironmentList, EnvironmentLoadReport, EnvironmentTest, ExecutionEnvironment } from '../core/models';
import { ActionDialogComponent, BadgeComponent } from '../shared/shared';

const EMPTY: EnvironmentInput = { name: '', url: '', token: '', workspaceRoot: '', tags: '', maxConcurrency: 1 };

@Component({
  selector: 'app-environments-page',
  standalone: true,
  imports: [FormsModule, NgTemplateOutlet, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule, BadgeComponent],
  template: `
    <div class="page">
      <div class="row">
        <h1>Ambientes de execução</h1>
        <span class="spacer"></span>
        <button mat-flat-button type="button" (click)="toggleForm()">{{ formOpen() ? 'Fechar' : 'Novo ambiente' }}</button>
      </div>
      <p class="muted">
        Cada ambiente é uma máquina com o <strong>host agent</strong> (<span class="mono">scripts/host-agent</span>) onde os agentes e o
        git de um projeto rodam, com as ferramentas (Node, .NET, Python…) que a stack dele precisa. O projeto escolhe o ambiente;
        sem escolha, vale o ambiente padrão do servidor.
      </p>

      @if (formOpen()) {
        <section class="card">
          <h2>{{ editingId() ? 'Editar ambiente' : 'Novo ambiente' }}</h2>
          <form (ngSubmit)="save()" #f="ngForm" class="grid">
            <mat-form-field appearance="outline">
              <mat-label>Nome</mat-label>
              <input matInput name="name" [(ngModel)]="draft.name" required placeholder="notebook-node22" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Endereço do host agent</mat-label>
              <input matInput name="url" [(ngModel)]="draft.url" required placeholder="http://192.168.0.20:8787" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>{{ editingId() ? 'Token (vazio mantém o atual)' : 'Token do host agent' }}</mat-label>
              <input matInput type="password" name="token" [(ngModel)]="draft.token" [required]="!editingId()" autocomplete="off" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Pasta dos clones nessa máquina</mat-label>
              <input matInput name="workspaceRoot" [(ngModel)]="draft.workspaceRoot" required placeholder="C:/agentforge ou /home/eu/agentforge" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Stack (rótulos, separados por vírgula)</mat-label>
              <input matInput name="tags" [(ngModel)]="draft.tags" placeholder="node22, dotnet8" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Tarefas ao mesmo tempo (1 a 32)</mat-label>
              <input matInput type="number" min="1" max="32" name="maxConcurrency" [(ngModel)]="draft.maxConcurrency" required />
            </mat-form-field>
            <div class="row">
              <button mat-flat-button type="submit" [disabled]="f.invalid || saving()">Salvar</button>
              <button mat-button type="button" (click)="closeForm()">Cancelar</button>
            </div>
          </form>
          <p class="muted small">
            O token é guardado criptografado e nunca volta para esta tela. A pasta precisa estar entre as permitidas
            (<span class="mono">HostAgent:Roots</span>) do host agent.
          </p>
        </section>
      }

      @if (load(); as report) {
        <section class="card" data-testid="load">
          <div class="row">
            <h2>Carga</h2>
            <span class="muted small">atualiza a cada 5 s</span>
            <span class="spacer"></span>
            <button mat-button type="button" (click)="refreshLoad()">Atualizar</button>
          </div>
          <table class="load">
            <thead><tr><th>Ambiente</th><th>Tarefas rodando</th><th>Na fila</th><th>Agentes agora</th><th>Projetos</th></tr></thead>
            <tbody>
              @for (r of report.environments; track r.name) {
                <tr data-testid="load-row">
                  <td>{{ r.name }}</td>
                  <td>{{ r.runningTasks }} / {{ r.maxConcurrency }}</td>
                  <td>@if (r.queuedTasks > 0) { <app-badge [label]="r.queuedTasks + ' esperando vaga'" tone="warn" /> } @else { 0 }</td>
                  <td>{{ r.runningAgents }}</td>
                  <td>{{ r.projectCount }}</td>
                </tr>
              }
            </tbody>
          </table>
          @for (w of report.waiting; track w.id) {
            <div class="alert-waiting" role="status" data-testid="waiting-row">
              <app-badge label="aguardando a máquina" tone="warn" />
              <strong>{{ w.title }}</strong> <span class="muted small">({{ w.project }}, {{ w.kind === 'demand' ? 'refinamento' : 'tarefa' }})</span>
              <span class="muted small">{{ w.reason }} · tentativa {{ w.attempts }}, próxima em {{ retryIn(w.retryAt) }}</span>
            </div>
          }
        </section>
      }

      @if (list(); as l) {
        <section class="card" data-testid="default-env">
          <div class="row">
            <h2>Padrão do servidor</h2>
            <app-badge [label]="l.default.mode === 'host' ? 'host agent' : 'container da API'" tone="info" />
            <span class="spacer"></span>
            <button mat-button type="button" [disabled]="testing() === 'default'" (click)="test('default')">Testar</button>
          </div>
          <p class="muted small">{{ l.default.description }}. Usado pelos projetos sem ambiente próprio.</p>
          @if (results()['default']; as r) {
            <ng-container *ngTemplateOutlet="result; context: { $implicit: r, showRoot: false }" />
          }
        </section>

        @for (e of l.items; track e.id) {
          <section class="card" data-testid="env">
            <div class="row">
              <h2>{{ e.name }}</h2>
              @for (t of tags(e); track t) { <app-badge [label]="t" /> }
              <span class="spacer"></span>
              <button mat-button type="button" [disabled]="testing() === e.id" (click)="test(e.id)">Testar</button>
              <a mat-button routerLink="/git" [queryParams]="{ environmentId: e.id }">Chave Git</a>
              <a mat-button routerLink="/agents" [queryParams]="{ environmentId: e.id }">Login do agente</a>
              <button mat-button type="button" (click)="edit(e)">Editar</button>
              <button mat-button type="button" (click)="remove(e)">Excluir</button>
            </div>
            <p class="muted small">
              <span class="mono">{{ e.url }}</span> · clones em <span class="mono">{{ e.workspaceRoot }}</span> ·
              {{ e.projectCount }} projeto(s) · até {{ e.maxConcurrency }} tarefa(s) ao mesmo tempo
            </p>
            @if (results()[e.id]; as r) {
              <ng-container *ngTemplateOutlet="result; context: { $implicit: r, showRoot: true }" />
            }
          </section>
        } @empty {
          <p class="muted">Nenhuma máquina cadastrada. Suba o host agent na máquina e cadastre-a aqui.</p>
        }
      } @else {
        <p class="muted">Carregando…</p>
      }
    </div>

    <ng-template #result let-r let-showRoot="showRoot">
      <div class="result" [class.ok]="r.reachable" [class.bad]="!r.reachable" role="status" data-testid="test-result">
        @if (!r.reachable) {
          <app-badge label="sem conexão" tone="bad" /> {{ r.error }}
        } @else {
          <app-badge label="conectado" tone="ok" />
          <app-badge [label]="r.containersEnabled ? 'containers habilitados' : 'sem containers'" [tone]="r.containersEnabled ? 'ok' : 'idle'" />
          @if (showRoot && !r.workspaceRootExists) {
            <span class="muted small">a pasta dos clones será criada no primeiro uso</span>
          }
          <ul class="tools">
            @for (t of r.tools; track t.tool) {
              <li><app-badge [label]="t.tool" [tone]="t.ok ? 'ok' : 'warn'" /> <span class="muted small">{{ t.detail }}</span></li>
            }
          </ul>
        }
      </div>
    </ng-template>
  `,
  styles: `
    .card { background: #1b1b1b; border-radius: 10px; padding: .75rem 1rem 1rem; margin-bottom: 1rem; }
    .card h2 { margin: .25rem 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr)); gap: .75rem 1rem; padding-top: .5rem; }
    .grid .row { grid-column: 1 / -1; }
    .small { font-size: .85rem; }
    .result { margin-top: .5rem; padding: .5rem .75rem; border-radius: 8px; background: #151515; }
    .result.ok { border-left: 4px solid #3f9f58; } .result.bad { border-left: 4px solid #c25555; }
    .load { width: 100%; border-collapse: collapse; margin-top: .5rem; }
    .load th, .load td { text-align: left; padding: .3rem .5rem; border-bottom: 1px solid #2a2a2a; }
    .alert-waiting { margin-top: .5rem; padding: .4rem .75rem; border-left: 4px solid #c9a227; background: #151515; border-radius: 6px; display: flex; gap: .5rem; flex-wrap: wrap; align-items: center; }
    .tools { list-style: none; padding: 0; margin: .5rem 0 0; display: flex; flex-wrap: wrap; gap: .25rem 1rem; }
  `,
})
export class EnvironmentsPage {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly list = signal<EnvironmentList | null>(null);
  readonly load = signal<EnvironmentLoadReport | null>(null);
  readonly results = signal<Record<string, EnvironmentTest>>({});
  readonly testing = signal<string | null>(null);
  readonly formOpen = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  draft: EnvironmentInput = { ...EMPTY };

  constructor() {
    this.loadList();
    this.refreshLoad();
    interval(5000).pipe(takeUntilDestroyed()).subscribe(() => this.refreshLoad());
  }

  refreshLoad(): void {
    this.api.environmentLoad().subscribe({ next: (r) => this.load.set(r), error: () => undefined });
  }

  /** "em 12 s" / "agora": quanto falta para a próxima tentativa de uma máquina fora do ar. */
  retryIn(at: string): string {
    const seconds = Math.round((new Date(at).getTime() - Date.now()) / 1000);
    return seconds > 1 ? `${seconds} s` : 'instantes';
  }

  tags(e: ExecutionEnvironment): string[] {
    return e.tags.split(',').map((t) => t.trim()).filter((t) => t.length > 0);
  }

  private loadList(): void {
    this.api.environments().subscribe((l) => this.list.set(l));
  }

  toggleForm(): void {
    if (this.formOpen()) this.closeForm();
    else this.open(null, { ...EMPTY });
  }

  edit(e: ExecutionEnvironment): void {
    this.open(e.id, { name: e.name, url: e.url, token: '', workspaceRoot: e.workspaceRoot, tags: e.tags, maxConcurrency: e.maxConcurrency });
  }

  private open(id: string | null, draft: EnvironmentInput): void {
    this.editingId.set(id);
    this.draft = draft;
    this.formOpen.set(true);
  }

  closeForm(): void {
    this.formOpen.set(false);
    this.editingId.set(null);
    this.draft = { ...EMPTY };
  }

  save(): void {
    this.saving.set(true);
    const id = this.editingId();
    const request = id ? this.api.updateEnvironment(id, this.draft) : this.api.createEnvironment(this.draft);
    request.subscribe({
      next: (saved) => {
        this.saving.set(false);
        this.closeForm();
        // o resultado de um teste antigo não vale mais depois de editar
        this.results.update(({ [saved.id]: _old, ...rest }) => rest);
        this.snack.open('Ambiente salvo.', 'OK', { duration: 3000 });
        this.loadList();
      },
      error: (err) => {
        this.saving.set(false);
        this.snack.open(this.message(err, 'Não foi possível salvar o ambiente.'), 'OK', { duration: 6000 });
      },
    });
  }

  test(id: string): void {
    this.testing.set(id);
    this.api.testEnvironment(id).subscribe({
      next: (r) => {
        this.testing.set(null);
        this.results.update((all) => ({ ...all, [id]: r }));
      },
      error: () => {
        this.testing.set(null);
        this.snack.open('Não foi possível testar o ambiente.', 'OK', { duration: 5000 });
      },
    });
  }

  remove(e: ExecutionEnvironment): void {
    this.dialog
      .open(ActionDialogComponent, {
        data: {
          title: 'Excluir ambiente',
          message: `Excluir "${e.name}"? Os clones nessa máquina não são apagados.`,
          confirmLabel: 'Excluir',
        },
      })
      .afterClosed()
      .subscribe((ok) => {
        if (!ok) return;
        this.api.deleteEnvironment(e.id).subscribe({
          next: () => this.loadList(),
          error: (err) => this.snack.open(this.message(err, 'Não foi possível excluir.'), 'OK', { duration: 6000 }),
        });
      });
  }

  private message(err: { error?: { error?: string; errors?: Record<string, string[]> } }, fallback: string): string {
    return err?.error?.error ?? Object.values(err?.error?.errors ?? {})[0]?.[0] ?? fallback;
  }
}
