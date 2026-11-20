import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../core/api.service';
import { ExecutionEnvironment, GitHubImportResult, GitHubRepo } from '../core/models';
import { BadgeComponent } from '../shared/shared';

/** Importar do GitHub: lista os repositórios do token (ou aceita dono/repo de um repositório público) e já clona. */
@Component({
  selector: 'app-github-import',
  standalone: true,
  imports: [FormsModule, BadgeComponent],
  template: `
    <div class="card card-p import-box">
      <div class="section-title">Importar do GitHub</div>
      <p style="font-size:13px;color:var(--text-2);margin:4px 0 14px">
        O token é usado só nesta tela, para listar os repositórios e autorizar a chave do AgentForge; ele não é guardado.
        Gere um em GitHub → Settings → Developer settings → Personal access tokens (escopo <span style="font-family:var(--mono)">repo</span>).
      </p>

      <div class="row-flex">
        <div class="form-group" style="flex:1">
          <div class="form-label">Token de acesso do GitHub</div>
          <input class="form-input" type="password" autocomplete="off" [(ngModel)]="token" data-testid="token" />
        </div>
        <button class="btn btn-secondary" type="button" style="align-self:flex-end" [disabled]="loading() || !token.trim()" (click)="list()">Listar meus repositórios</button>
      </div>

      @if (error()) {
        <p style="color:var(--danger);font-size:13px;margin-top:8px" data-testid="error">{{ error() }}</p>
      }

      @if (repos().length) {
        <div class="form-group" style="margin-top:12px">
          <div class="form-label">Filtrar</div>
          <input class="form-input" style="width:100%" [ngModel]="filter()" (ngModelChange)="filter.set($event)" data-testid="filter" />
        </div>
        <div class="repos" data-testid="repos">
          @for (r of filtered(); track r.fullName) {
            <button type="button" class="repo" [class.selected]="repository === r.fullName" (click)="select(r)">
              <span class="repo-name">{{ r.fullName }}</span>
              <app-badge [label]="r.private ? 'privado' : 'público'" [tone]="r.private ? 'warn' : 'ok'" />
              <app-badge [label]="'base: ' + r.defaultBranch" />
              @if (r.description) { <span style="flex-basis:100%;font-size:12px;color:var(--text-2)">{{ r.description }}</span> }
            </button>
          } @empty {
            <p style="font-size:13px;color:var(--text-2)">Nenhum repositório corresponde ao filtro.</p>
          }
        </div>
      }

      <div class="form-group" style="margin-top:12px">
        <div class="form-label">Repositório (dono/nome ou URL)</div>
        <input class="form-input" style="width:100%" [(ngModel)]="repository" placeholder="octocat/Hello-World" data-testid="repository" />
      </div>

      <div class="form-grid" style="margin-top:10px">
        <div class="form-group">
          <div class="form-label">Comando de build</div>
          <input class="form-input" [(ngModel)]="buildCommand" placeholder="dotnet build" />
        </div>
        <div class="form-group">
          <div class="form-label">Comando de teste</div>
          <input class="form-input" [(ngModel)]="testCommand" placeholder="dotnet test" />
        </div>
      </div>

      @if (environments().length > 0) {
        <div class="form-group" style="margin-top:10px">
          <div class="form-label">Ambiente de execução</div>
          <select class="form-input" [(ngModel)]="environmentId" data-testid="environment">
            <option [ngValue]="null">Padrão do servidor</option>
            @for (e of environments(); track e.id) { <option [ngValue]="e.id">{{ e.name }}</option> }
          </select>
        </div>
      }

      <div style="display:flex;flex-direction:column;gap:8px;margin-top:14px">
        <label class="checkbox-row">
          <input type="checkbox" [(ngModel)]="addDeployKey" />
          Autorizar a chave SSH do AgentForge no repositório (deploy key somente leitura, só para repositórios privados)
        </label>
        <label class="checkbox-row">
          <input type="checkbox" [(ngModel)]="autoMode" />
          Modo automático (sem aprovações humanas)
        </label>
      </div>

      @if (result(); as r) {
        <div class="result-box" data-testid="result">
          <strong>{{ r.project.name }}</strong> importado e clonado.
          @for (n of r.notes; track n) { <div style="font-size:12px;color:var(--text-2)">{{ n }}</div> }
        </div>
      }

      <div style="display:flex;gap:10px;justify-content:flex-start;padding-top:12px;border-top:1px solid var(--border);margin-top:14px">
        <button class="btn btn-primary" type="button" [disabled]="importing() || !repository.trim()" (click)="doImport()" data-testid="import">
          {{ importing() ? 'Clonando…' : 'Importar e clonar' }}
        </button>
        <button class="btn btn-secondary" type="button" (click)="closed.emit()">Cancelar</button>
      </div>
    </div>
  `,
  styles: `
    .import-box { margin: 16px 0; }
    .row-flex { display: flex; gap: 10px; align-items: flex-start; flex-wrap: wrap; }
    .repos { display: grid; gap: 6px; max-height: 16rem; overflow-y: auto; margin-top: 8px; }
    .repo {
      display: flex; gap: 8px; align-items: center; flex-wrap: wrap; text-align: left;
      padding: 8px 12px; background: var(--bg-2); color: inherit;
      border: 1px solid var(--border); border-radius: var(--radius-sm);
      cursor: pointer; min-height: 40px; transition: border-color .15s, background .15s;
      font: inherit; font-size: 13px;
    }
    .repo:hover { border-color: var(--border-active); background: var(--bg-hover); }
    .repo.selected { border-color: var(--accent); background: var(--accent-dim); }
    .repo-name { font-weight: 600; font-family: var(--mono); font-size: 13px; }
    .checkbox-row { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--text-1); cursor: pointer; }
    .checkbox-row input[type="checkbox"] { width: 16px; height: 16px; accent-color: var(--accent); cursor: pointer; }
    .result-box { margin-top: 12px; padding: 10px 14px; background: rgba(52,211,153,.08); border-radius: var(--radius-sm); font-size: 13px; }
  `,
})
export class GithubImportComponent {
  private readonly api = inject(ApiService);

  readonly imported = output<GitHubImportResult>();
  readonly closed = output<void>();
  readonly environments = input<ExecutionEnvironment[]>([]);

  token = '';
  repository = '';
  buildCommand = '';
  testCommand = '';
  addDeployKey = true;
  autoMode = false;
  environmentId: string | null = null;

  readonly repos = signal<GitHubRepo[]>([]);
  readonly filter = signal('');
  readonly loading = signal(false);
  readonly importing = signal(false);
  readonly error = signal<string | null>(null);
  readonly result = signal<GitHubImportResult | null>(null);

  readonly filtered = computed(() => {
    const f = this.filter().trim().toLowerCase();
    return f ? this.repos().filter((r) => r.fullName.toLowerCase().includes(f) || (r.description ?? '').toLowerCase().includes(f)) : this.repos();
  });

  list(): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.githubRepos(this.token.trim()).subscribe({
      next: (repos) => {
        this.loading.set(false);
        this.repos.set(repos);
        if (!repos.length) this.error.set('Esse token não enxerga nenhum repositório.');
      },
      error: (e: HttpErrorResponse) => {
        this.loading.set(false);
        this.repos.set([]);
        this.error.set(message(e, 'Não foi possível listar os repositórios.'));
      },
    });
  }

  select(r: GitHubRepo): void {
    this.repository = r.fullName;
  }

  doImport(): void {
    this.importing.set(true);
    this.error.set(null);
    this.result.set(null);
    const token = this.token.trim();
    this.api
      .githubImport({
        repository: this.repository.trim(), ...(token ? { token } : {}), addDeployKey: this.addDeployKey,
        buildCommand: this.buildCommand, testCommand: this.testCommand, autoMode: this.autoMode,
        environmentId: this.environmentId,
      })
      .subscribe({
        next: (r) => {
          this.importing.set(false);
          this.token = '';
          this.result.set(r);
          this.imported.emit(r);
        },
        error: (e: HttpErrorResponse) => {
          this.importing.set(false);
          this.error.set(message(e, 'Não foi possível importar o repositório.'));
        },
      });
  }
}

function message(e: HttpErrorResponse, fallback: string): string {
  const body = e.error as { error?: string; errors?: Record<string, string[]> } | null;
  return body?.error ?? Object.values(body?.errors ?? {}).flat()[0] ?? fallback;
}
