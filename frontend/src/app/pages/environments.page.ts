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
      <div class="page-header">
        <div>
          <div class="page-title">Ambientes de execução</div>
          <div class="page-subtitle">Máquinas e recursos para rodar agentes</div>
        </div>
        <div class="header-actions">
          <button class="btn btn-primary" type="button" (click)="toggleForm()">{{ formOpen() ? 'Fechar' : 'Novo ambiente' }}</button>
        </div>
      </div>

      @if (formOpen()) {
        <div class="card card-p" style="margin-bottom:16px">
          <div style="font-size:15px;font-weight:600;margin-bottom:12px">{{ editingId() ? 'Editar ambiente' : 'Novo ambiente' }}</div>
          <form (ngSubmit)="save()" #f="ngForm" class="form-grid">
            <div class="form-group">
              <div class="form-label">Nome</div>
              <input class="form-input" name="name" [(ngModel)]="draft.name" required placeholder="notebook-node22" />
            </div>
            <div class="form-group">
              <div class="form-label">Endereço do host agent</div>
              <input class="form-input" name="url" [(ngModel)]="draft.url" required placeholder="http://192.168.0.20:8787" />
            </div>
            <div class="form-group">
              <div class="form-label">{{ editingId() ? 'Token (vazio mantém o atual)' : 'Token do host agent' }}</div>
              <input class="form-input" type="password" name="token" [(ngModel)]="draft.token" [required]="!editingId()" autocomplete="off" />
            </div>
            <div class="form-group">
              <div class="form-label">Pasta dos clones nessa máquina</div>
              <input class="form-input" name="workspaceRoot" [(ngModel)]="draft.workspaceRoot" required placeholder="C:/agentforge ou /home/eu/agentforge" />
            </div>
            <div class="form-group">
              <div class="form-label">Stack (rótulos)</div>
              <input class="form-input" name="tags" [(ngModel)]="draft.tags" placeholder="node22, dotnet8" />
            </div>
            <div class="form-group">
              <div class="form-label">Tarefas ao mesmo tempo (1 a 32)</div>
              <input class="form-input" type="number" min="1" max="32" name="maxConcurrency" [(ngModel)]="draft.maxConcurrency" required />
            </div>
            <div class="full" style="display:flex;gap:8px;margin-top:4px">
              <button type="submit" class="btn btn-primary btn-sm" [disabled]="f.invalid || saving()">Salvar</button>
              <button type="button" class="btn btn-secondary btn-sm" (click)="closeForm()">Cancelar</button>
            </div>
          </form>
          <div style="font-size:12px;color:var(--text-2);margin-top:12px">
            O token é guardado criptografado e nunca volta para esta tela.
          </div>
        </div>
      }

      @if (load(); as report) {
        <div class="section-title">Carga atual</div>
        <div class="card" style="overflow:hidden;margin-bottom:24px" data-testid="load">
          <table class="env-table">
            <thead>
              <tr>
                <th style="text-align:left;padding:10px 14px">Ambiente</th>
                <th style="text-align:center;padding:10px">Rodando</th>
                <th style="text-align:center;padding:10px">Fila</th>
                <th style="text-align:center;padding:10px">Agentes</th>
                <th style="text-align:center;padding:10px">Projetos</th>
              </tr>
            </thead>
            <tbody>
              @for (r of report.environments; track r.name) {
                <tr data-testid="load-row">
                  <td style="padding:10px 14px;font-weight:500">{{ r.name }}</td>
                  <td style="text-align:center;padding:10px"><span class="badge badge-accent">{{ r.runningTasks }} / {{ r.maxConcurrency }}</span></td>
                  <td style="text-align:center;padding:10px">{{ r.queuedTasks }}</td>
                  <td style="text-align:center;padding:10px">{{ r.runningAgents }}</td>
                  <td style="text-align:center;padding:10px">{{ r.projectCount }}</td>
                </tr>
              }
            </tbody>
          </table>
          @for (w of report.waiting; track w.id) {
            <div class="alert-waiting" role="status" data-testid="waiting-row">
              <app-badge [label]="w.cause === 'usage-limit' ? 'aguardando o limite de uso' : 'aguardando a máquina'" tone="warn" />
              <strong>{{ w.title }}</strong> <span style="font-size:12px;color:var(--text-2)">({{ w.project }}, {{ w.kind === 'demand' ? 'refinamento' : 'tarefa' }})</span>
              <span style="font-size:12px;color:var(--text-2)">{{ w.reason }} · {{ w.cause === 'usage-limit' ? 'volta a tentar' : 'tentativa ' + w.attempts + ', próxima' }} em {{ retryIn(w.retryAt) }}</span>
            </div>
          }
        </div>
      }

      @if (list(); as l) {
        <div class="card card-p" style="margin-bottom:16px" data-testid="default-env">
          <div style="display:flex;align-items:center;gap:8px;margin-bottom:12px">
            <div style="font-size:15px;font-weight:600">Padrão do servidor</div>
            <app-badge [label]="l.default.mode === 'host' ? 'host agent' : 'container da API'" tone="info" />
            <span style="flex:1"></span>
            <button class="btn btn-secondary btn-sm" type="button" [disabled]="testing() === 'default'" (click)="test('default')">Testar</button>
          </div>
          <div style="font-size:12px;color:var(--text-2)">{{ l.default.description }}. Usado pelos projetos sem ambiente próprio.</div>
          @if (results()['default']; as r) {
            <ng-container *ngTemplateOutlet="result; context: { $implicit: r, showRoot: false }" />
          }
        </div>

        <div class="grid-auto">
          @for (e of l.items; track e.id) {
            <div class="card card-p" data-testid="env">
              <div style="display:flex;align-items:center;gap:8px;margin-bottom:12px">
                <div style="font-size:15px;font-weight:600">{{ e.name }}</div>
                @for (t of tags(e); track t) { <span class="badge badge-idle">{{ t }}</span> }
              </div>
              <div style="font-size:12px;color:var(--text-2);line-height:1.8;margin-bottom:12px">
                URL: <span style="font-family:var(--mono)">{{ e.url }}</span><br>
                Concorrência: {{ e.maxConcurrency }}<br>
                Projetos: {{ e.projectCount }}
              </div>
              <div style="display:flex;gap:6px;flex-wrap:wrap">
                <button class="btn btn-secondary btn-sm" type="button" [disabled]="testing() === e.id" (click)="test(e.id)">Testar</button>
                <a class="btn btn-secondary btn-sm" routerLink="/git" [queryParams]="{ environmentId: e.id }">Chave SSH</a>
                <a class="btn btn-secondary btn-sm" routerLink="/agents" [queryParams]="{ environmentId: e.id }">Login agente</a>
                <button class="btn btn-secondary btn-sm" type="button" (click)="edit(e)">Editar</button>
                <button class="btn btn-danger btn-sm" type="button" (click)="remove(e)">Excluir</button>
              </div>
              @if (results()[e.id]; as r) {
                <ng-container *ngTemplateOutlet="result; context: { $implicit: r, showRoot: true }" />
              }
            </div>
          } @empty {
            <p style="color:var(--text-2)">Nenhuma máquina cadastrada. Suba o host agent na máquina e cadastre-a aqui.</p>
          }
        </div>
      } @else {
        <p style="color:var(--text-2)">Carregando…</p>
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
            <span style="font-size:12px;color:var(--text-2)">a pasta dos clones será criada no primeiro uso</span>
          }
          <ul class="tools">
            @for (t of r.tools; track t.tool) {
              <li><app-badge [label]="t.tool" [tone]="t.ok ? 'ok' : 'warn'" /> <span style="font-size:12px;color:var(--text-2)">{{ t.detail }}</span></li>
            }
          </ul>
        }
      </div>
    </ng-template>
  `,
  styles: `
    .env-table { width: 100%; font-size: 13px; border-collapse: collapse; }
    .env-table thead tr { border-bottom: 1px solid var(--border); }
    .env-table th { font-weight: 600; color: var(--text-2); font-size: 12px; }
    .env-table tbody tr { border-bottom: 1px solid var(--border); }
    .env-table tbody tr:last-child { border-bottom: none; }
    .env-table tbody tr:hover { background: var(--bg-hover); }
    .result { margin-top: 12px; padding: 10px 14px; border-radius: var(--radius-sm); background: var(--bg-2); display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
    .result.ok { border-left: 4px solid var(--success); }
    .result.bad { border-left: 4px solid var(--danger); }
    .alert-waiting { margin: 8px 14px; padding: 10px 14px; border-left: 4px solid var(--warning); background: rgba(251,191,36,.08); border-radius: var(--radius-sm); display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .tools { list-style: none; padding: 0; margin: 4px 0 0; display: flex; flex-wrap: wrap; gap: 4px 12px; }
    @media (max-width: 639px) { .env-table { display: block; overflow-x: auto; } }
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
