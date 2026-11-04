import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { ApiService } from '../core/api.service';
import { GitHubImportResult, GitHubRepo } from '../core/models';
import { BadgeComponent } from '../shared/shared';

/** Importar do GitHub: lista os repositórios do token (ou aceita dono/repo de um repositório público) e já clona. */
@Component({
  selector: 'app-github-import',
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatProgressSpinnerModule, BadgeComponent],
  template: `
    <mat-card class="box">
      <mat-card-header><mat-card-title>Importar do GitHub</mat-card-title></mat-card-header>
      <mat-card-content>
        <p class="muted">
          O token é usado só nesta tela, para listar os repositórios e autorizar a chave do AgentForge; ele não é guardado.
          Gere um em GitHub → Settings → Developer settings → Personal access tokens (escopo <span class="mono">repo</span>).
        </p>
        <div class="row">
          <mat-form-field appearance="outline" class="grow">
            <mat-label>Token de acesso do GitHub</mat-label>
            <input matInput type="password" name="ghToken" autocomplete="off" [(ngModel)]="token" data-testid="token" />
          </mat-form-field>
          <button mat-flat-button type="button" [disabled]="loading() || !token.trim()" (click)="list()">Listar meus repositórios</button>
        </div>

        @if (error()) {
          <p class="error" data-testid="error">{{ error() }}</p>
        }

        @if (repos().length) {
          <mat-form-field appearance="outline" class="full">
            <mat-label>Filtrar</mat-label>
            <input matInput name="ghFilter" [ngModel]="filter()" (ngModelChange)="filter.set($event)" data-testid="filter" />
          </mat-form-field>
          <div class="repos" data-testid="repos">
            @for (r of filtered(); track r.fullName) {
              <button type="button" class="repo" [class.selected]="repository === r.fullName" (click)="select(r)">
                <span class="name mono">{{ r.fullName }}</span>
                <app-badge [label]="r.private ? 'privado' : 'público'" [tone]="r.private ? 'warn' : 'ok'" />
                <app-badge [label]="'base: ' + r.defaultBranch" />
                @if (r.description) { <span class="muted desc">{{ r.description }}</span> }
              </button>
            } @empty {
              <p class="muted">Nenhum repositório corresponde ao filtro.</p>
            }
          </div>
        }

        <div class="row">
          <mat-form-field appearance="outline" class="grow">
            <mat-label>Repositório (dono/nome ou URL)</mat-label>
            <input matInput name="ghRepo" [(ngModel)]="repository" placeholder="octocat/Hello-World" data-testid="repository" />
          </mat-form-field>
        </div>
        <div class="row">
          <mat-form-field appearance="outline" class="grow">
            <mat-label>Comando de build</mat-label>
            <input matInput name="ghBuild" [(ngModel)]="buildCommand" placeholder="dotnet build" />
          </mat-form-field>
          <mat-form-field appearance="outline" class="grow">
            <mat-label>Comando de teste</mat-label>
            <input matInput name="ghTest" [(ngModel)]="testCommand" placeholder="dotnet test" />
          </mat-form-field>
        </div>
        <mat-checkbox name="ghDeploy" [(ngModel)]="addDeployKey">
          Autorizar a chave SSH do AgentForge no repositório (deploy key somente leitura, só para repositórios privados)
        </mat-checkbox>
        <mat-checkbox name="ghAuto" [(ngModel)]="autoMode">Modo automático (sem aprovações humanas)</mat-checkbox>

        @if (result(); as r) {
          <div class="result" data-testid="result">
            <strong>{{ r.project.name }}</strong> importado e clonado.
            @for (n of r.notes; track n) { <div class="muted">{{ n }}</div> }
          </div>
        }
      </mat-card-content>
      <mat-card-actions>
        <button mat-flat-button type="button" [disabled]="importing() || !repository.trim()" (click)="doImport()" data-testid="import">
          @if (importing()) { <mat-spinner diameter="18" /> Clonando… } @else { Importar e clonar }
        </button>
        <button mat-button type="button" (click)="closed.emit()">Cancelar</button>
      </mat-card-actions>
    </mat-card>
  `,
  styles: `
    .box { margin: 1rem 0; }
    .row { display: flex; gap: .75rem; flex-wrap: wrap; align-items: baseline; }
    .grow { flex: 1 1 18rem; }
    .full { width: 100%; }
    .repos { display: grid; gap: .35rem; max-height: 16rem; overflow: auto; margin-bottom: 1rem; }
    .repo { display: flex; gap: .5rem; align-items: center; flex-wrap: wrap; text-align: left; padding: .5rem .75rem;
      background: transparent; color: inherit; border: 1px solid rgba(255,255,255,.18); border-radius: 6px; cursor: pointer; }
    .repo.selected { border-color: #7aa7ff; background: rgba(122,167,255,.12); }
    .name { font-weight: 600; }
    .desc { flex-basis: 100%; }
    .error { color: #ff8a80; }
    .result { margin-top: .75rem; }
    mat-card-actions { display: flex; gap: .5rem; padding: 0 1rem 1rem; }
  `,
})
export class GithubImportComponent {
  private readonly api = inject(ApiService);

  /** Emite o resultado quando o projeto foi criado e clonado. */
  readonly imported = output<GitHubImportResult>();
  readonly closed = output<void>();

  token = '';
  repository = '';
  buildCommand = '';
  testCommand = '';
  addDeployKey = true;
  autoMode = false;

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
