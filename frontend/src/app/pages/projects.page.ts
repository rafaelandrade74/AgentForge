import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { ExecutionEnvironment, GitHubImportResult, Project, ProjectGroup, ProjectGroupInput, ProjectInput, StackProfile } from '../core/models';
import { ActionDialogComponent, BadgeComponent } from '../shared/shared';
import { GithubImportComponent } from './github-import.component';

const EMPTY: ProjectInput = { name: '', repositoryUrl: '', baseBranch: 'main', buildCommand: '', testCommand: '', autoMode: false, environmentId: null, stackProfileId: null, projectGroupId: null, roleEnvironments: {}, pullRequestBaseBranch: '', hasGitHubToken: false };

@Component({
  selector: 'app-projects-page',
  standalone: true,
  imports: [FormsModule, RouterLink, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, BadgeComponent, GithubImportComponent, NgTemplateOutlet],
  template: `
    <div class="page">
      <div class="row">
        <h1>Projetos</h1>
        <span class="spacer"></span>
        <button mat-stroked-button type="button" (click)="toggleGroupForm()">Novo grupo</button>
        <button mat-stroked-button type="button" (click)="importOpen.set(!importOpen())">Importar do GitHub</button>
        <button mat-flat-button type="button" (click)="toggleForm()">{{ formOpen() ? 'Fechar' : 'Novo projeto' }}</button>
      </div>

      @if (importOpen()) {
        <app-github-import [environments]="environments()" (imported)="onImported($event)" (closed)="importOpen.set(false)" />
      }

      @if (formOpen()) {
        <mat-card class="form">
          <mat-card-header><mat-card-title>{{ editingId() ? 'Editar projeto' : 'Cadastrar projeto' }}</mat-card-title></mat-card-header>
          <mat-card-content>
            <form (ngSubmit)="save()" #f="ngForm" class="grid">
              <mat-form-field appearance="outline">
                <mat-label>Nome</mat-label>
                <input matInput name="name" [(ngModel)]="draft.name" required />
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>URL do repositório Git</mat-label>
                <input matInput name="repositoryUrl" [(ngModel)]="draft.repositoryUrl" required />
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Branch base</mat-label>
                <input matInput name="baseBranch" [(ngModel)]="draft.baseBranch" />
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Comando de build</mat-label>
                <input matInput name="buildCommand" [(ngModel)]="draft.buildCommand" placeholder="dotnet build" />
              </mat-form-field>
              <mat-form-field appearance="outline">
                <mat-label>Comando de teste</mat-label>
                <input matInput name="testCommand" [(ngModel)]="draft.testCommand" placeholder="dotnet test" />
              </mat-form-field>
              @if (groups().length > 0) {
                <mat-form-field appearance="outline">
                  <mat-label>Grupo</mat-label>
                  <mat-select name="projectGroupId" [(ngModel)]="draft.projectGroupId">
                    <mat-option [value]="null">Sem grupo</mat-option>
                    @for (g of groups(); track g.id) { <mat-option [value]="g.id">{{ g.name }}</mat-option> }
                  </mat-select>
                </mat-form-field>
              }
              <mat-form-field appearance="outline">
                <mat-label>Ambiente de execução</mat-label>
                <mat-select name="environmentId" [(ngModel)]="draft.environmentId">
                  <mat-option [value]="null">Padrão do servidor</mat-option>
                  @for (e of environments(); track e.id) { <mat-option [value]="e.id">{{ e.name }}</mat-option> }
                </mat-select>
              </mat-form-field>
              @if (draft.environmentId) {
                <mat-form-field appearance="outline">
                  <mat-label>Perfil de stack (container)</mat-label>
                  <mat-select name="stackProfileId" [(ngModel)]="draft.stackProfileId">
                    <mat-option [value]="null">Nenhum (roda direto na máquina)</mat-option>
                    @for (s of profiles(); track s.id) { <mat-option [value]="s.id">{{ s.name }}</mat-option> }
                  </mat-select>
                </mat-form-field>
              }
              <mat-form-field appearance="outline">
                <mat-label>Branch de destino dos PRs</mat-label>
                <input matInput name="pullRequestBaseBranch" [(ngModel)]="draft.pullRequestBaseBranch" placeholder="develop (vazio = a branch base)" data-testid="pr-branch" />
              </mat-form-field>
              <div class="gh-token" data-testid="gh-token">
                <mat-form-field appearance="outline">
                  <mat-label>{{ draft.hasGitHubToken && !tokenCleared ? 'Token do GitHub para PRs (vazio mantém o guardado)' : 'Token do GitHub para PRs' }}</mat-label>
                  <input matInput type="password" name="githubToken" autocomplete="off" [ngModel]="tokenInput" (ngModelChange)="setToken($event)"
                    placeholder="github_pat_… (Contents e Pull requests de escrita, só neste repositório)" />
                </mat-form-field>
                @if (draft.hasGitHubToken && !tokenCleared) {
                  <p class="muted small">Há um token guardado (criptografado; nunca volta para esta tela).
                    <button mat-button type="button" (click)="clearToken()">Apagar token</button></p>
                } @else if (tokenCleared) {
                  <p class="muted small">O token guardado será apagado ao salvar.</p>
                }
              </div>
              @if (environments().length > 0) {
                <div class="roles-env" data-testid="role-envs">
                  <p class="muted small">Revisão em outra máquina (opcional): Tech Lead e Security recebem só o diff, sem o repositório. Dev e QA ficam sempre no ambiente do projeto.</p>
                  @for (role of movableRoles; track role.key) {
                    <mat-form-field appearance="outline" subscriptSizing="dynamic">
                      <mat-label>{{ role.label }} roda em</mat-label>
                      <mat-select [name]="'role-' + role.key" [ngModel]="roleEnv(role.key)" (ngModelChange)="setRoleEnv(role.key, $event)">
                        <mat-option [value]="null">Mesmo ambiente do projeto</mat-option>
                        @for (e of environments(); track e.id) { <mat-option [value]="e.id">{{ e.name }}</mat-option> }
                      </mat-select>
                    </mat-form-field>
                  }
                </div>
              }
              @if (environmentChanged()) {
                <p class="muted small env-hint" data-testid="env-change-hint">
                  O clone atual continua na máquina anterior (não é movido): o próximo uso clona o repositório na nova máquina, que precisa
                  ter acesso a ele — gere a chave dela na tela Git e autorize-a no GitHub.
                </p>
              }
              <mat-checkbox name="autoMode" [(ngModel)]="draft.autoMode">
                Modo automático (sem aprovações humanas)
              </mat-checkbox>
              <div class="row">
                <button mat-flat-button type="submit" [disabled]="f.invalid || saving()">Salvar</button>
                <button mat-button type="button" (click)="closeForm()">Cancelar</button>
              </div>
            </form>
          </mat-card-content>
        </mat-card>
      }

      @if (groupFormOpen()) {
        <mat-card class="form">
          <mat-card-header><mat-card-title>{{ editingGroupId() ? 'Editar grupo' : 'Novo grupo' }}</mat-card-title></mat-card-header>
          <mat-card-content>
            <form (ngSubmit)="saveGroup()" class="grid" style="padding-top:.75rem">
              <mat-form-field appearance="outline"><mat-label>Nome</mat-label>
                <input matInput [(ngModel)]="groupDraft.name" name="gname" required /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Descrição</mat-label>
                <input matInput [(ngModel)]="groupDraft.description" name="gdesc" /></mat-form-field>
              <div class="row">
                <button mat-flat-button type="submit" [disabled]="!groupDraft.name">Salvar</button>
                <button mat-button type="button" (click)="groupFormOpen.set(false)">Cancelar</button>
              </div>
            </form>
          </mat-card-content>
        </mat-card>
      }

      @for (g of groups(); track g.id) {
        <section class="group-section">
          <div class="row group-header">
            <h2><a [routerLink]="['/groups', g.id]">{{ g.name }}</a></h2>
            @if (g.description) { <span class="muted small">{{ g.description }}</span> }
            <span class="spacer"></span>
            <button mat-button type="button" (click)="editGroup(g)">Editar</button>
            <button mat-button type="button" (click)="removeGroup(g)">Excluir</button>
          </div>
          <div class="cards">
            @for (p of projectsOfGroup(g.id); track p.id) {
              <ng-container *ngTemplateOutlet="projectCard; context: { $implicit: p }"></ng-container>
            } @empty {
              <p class="muted small">Nenhum projeto neste grupo.</p>
            }
          </div>
        </section>
      }

      @if (ungroupedProjects().length > 0 || (groups().length === 0 && loaded())) {
        @if (groups().length > 0) { <h2 class="section-title">Sem grupo</h2> }
        <div class="cards">
          @for (p of ungroupedProjects(); track p.id) {
            <ng-container *ngTemplateOutlet="projectCard; context: { $implicit: p }"></ng-container>
          } @empty {
            @if (loaded() && groups().length === 0) {
              <p class="muted">Nenhum projeto ainda. Cadastre o primeiro para começar.</p>
            }
          }
        </div>
      }

      <ng-template #projectCard let-p>
        <mat-card>
          <mat-card-header>
            <mat-card-title><a [routerLink]="['/projects', p.id]">{{ p.name }}</a></mat-card-title>
            <mat-card-subtitle class="mono">{{ p.repositoryUrl }}</mat-card-subtitle>
          </mat-card-header>
          <mat-card-content>
            <div class="row">
              <app-badge [label]="'base: ' + p.baseBranch" />
              <app-badge [label]="p.autoMode ? 'automático' : 'com aprovações'" [tone]="p.autoMode ? 'warn' : 'ok'" />
              @if (environmentName(p); as env) { <app-badge [label]="'ambiente: ' + env" tone="info" /> }
              @if (profileName(p); as prof) { <app-badge [label]="'perfil: ' + prof" tone="info" /> }
            </div>
          </mat-card-content>
          <mat-card-actions>
            <a mat-button [routerLink]="['/projects', p.id]">Abrir</a>
            <a mat-button [routerLink]="['/projects', p.id, 'demands', 'new']">Nova demanda</a>
            <button mat-button type="button" (click)="edit(p)">Editar</button>
            <button mat-button type="button" (click)="remove(p)">Excluir</button>
          </mat-card-actions>
        </mat-card>
      </ng-template>
    </div>
  `,
  styles: `
    .form { margin: 1rem 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr)); gap: .75rem 1rem; padding-top: 1rem; }
    .grid mat-checkbox, .grid .row { grid-column: 1 / -1; }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(22rem, 1fr)); gap: 1rem; margin-top: .75rem; }
    mat-card { border-left: 3px solid var(--accent) !important; transition: box-shadow var(--transition), border-color var(--transition) !important; }
    mat-card:hover { box-shadow: var(--shadow-md) !important; border-left-color: var(--accent-hover) !important; }
    mat-card-title a { text-decoration: none; color: inherit; font-size: 1.05rem; }
    mat-card-title a:hover { color: var(--accent); }
    mat-card-subtitle { font-size: .78rem !important; }
    .group-section { margin-top: 2rem; }
    .group-header { padding-bottom: .5rem; border-bottom: 1px solid var(--border); }
    .group-header h2 { margin: 0; font-size: 1.1rem; }
    .group-header h2 a { text-decoration: none; color: inherit; }
    .group-header h2 a:hover { color: var(--accent); }
    .section-title { margin-top: 2rem; font-size: .7rem; font-weight: 600; text-transform: uppercase; letter-spacing: .08em; color: var(--text-tertiary); }
    @media (max-width: 639px) { .cards { grid-template-columns: 1fr; } }
  `,
})
export class ProjectsPage {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly projects = signal<Project[]>([]);
  readonly groups = signal<ProjectGroup[]>([]);
  readonly environments = signal<ExecutionEnvironment[]>([]);
  readonly profiles = signal<StackProfile[]>([]);
  readonly groupFormOpen = signal(false);
  readonly editingGroupId = signal<string | null>(null);
  groupDraft: ProjectGroupInput = { name: '', description: null };

  readonly ungroupedProjects = computed(() => this.projects().filter(p => !p.projectGroupId));
  projectsOfGroup(groupId: string): Project[] { return this.projects().filter(p => p.projectGroupId === groupId); }
  /** Só estes papéis podem rodar em outra máquina (revisam pelo diff); Dev e QA precisam do worktree do projeto. */
  readonly movableRoles = [{ key: 'TechLead', label: 'Tech Lead' }, { key: 'Security', label: 'Security' }];

  roleEnv(role: string): string | null {
    return this.draft.roleEnvironments?.[role] ?? null;
  }

  setRoleEnv(role: string, environmentId: string | null): void {
    this.draft.roleEnvironments = { ...(this.draft.roleEnvironments ?? {}), [role]: environmentId };
  }
  readonly loaded = signal(false);
  readonly formOpen = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  readonly importOpen = signal(false);
  private readonly router = inject(Router);
  draft: ProjectInput = { ...EMPTY };
  private originalEnvironmentId: string | null = null;

  constructor() {
    this.load();
    this.api.environments().subscribe((l) => this.environments.set(l.items));
    this.api.stackProfiles().subscribe((s) => this.profiles.set(s));
  }

  private load(): void {
    this.api.projects().subscribe((p) => { this.projects.set(p); this.loaded.set(true); });
    this.api.projectGroups().subscribe((g) => this.groups.set(g));
  }

  toggleGroupForm(): void {
    if (this.groupFormOpen()) { this.groupFormOpen.set(false); return; }
    this.editingGroupId.set(null);
    this.groupDraft = { name: '', description: null };
    this.groupFormOpen.set(true);
  }

  editGroup(g: ProjectGroup): void {
    this.editingGroupId.set(g.id);
    this.groupDraft = { name: g.name, description: g.description };
    this.groupFormOpen.set(true);
  }

  saveGroup(): void {
    const id = this.editingGroupId();
    const req = id ? this.api.updateProjectGroup(id, this.groupDraft) : this.api.createProjectGroup(this.groupDraft);
    req.subscribe({ next: () => { this.groupFormOpen.set(false); this.load(); this.snack.open('Grupo salvo.', 'OK', { duration: 3000 }); },
      error: () => this.snack.open('Não foi possível salvar o grupo.', 'OK', { duration: 5000 }) });
  }

  removeGroup(g: ProjectGroup): void {
    this.dialog.open(ActionDialogComponent, {
      data: { title: 'Excluir grupo', message: `Excluir "${g.name}"? Os projetos serão desagrupados (não excluídos).`, confirmLabel: 'Excluir' },
    }).afterClosed().subscribe(ok => { if (ok) this.api.deleteProjectGroup(g.id).subscribe(() => this.load()); });
  }

  onImported(r: GitHubImportResult): void {
    this.snack.open(`Projeto "${r.project.name}" importado e clonado.`, 'OK', { duration: 4000 });
    void this.router.navigate(['/projects', r.project.id]);
  }

  profileName(p: Project): string | null {
    return p.stackProfileId ? (this.profiles().find((s) => s.id === p.stackProfileId)?.name ?? 'perfil removido') : null;
  }

  environmentName(p: Project): string | null {
    return p.environmentId ? (this.environments().find((e) => e.id === p.environmentId)?.name ?? "ambiente removido") : null;
  }

  toggleForm(): void {
    if (this.formOpen()) this.closeForm();
    else this.openForm(null, { ...EMPTY });
  }

  edit(p: Project): void {
    this.openForm(p.id, { name: p.name, repositoryUrl: p.repositoryUrl, baseBranch: p.baseBranch, buildCommand: p.buildCommand, testCommand: p.testCommand, autoMode: p.autoMode, environmentId: p.environmentId ?? null, stackProfileId: p.stackProfileId ?? null, projectGroupId: p.projectGroupId ?? null, roleEnvironments: p.roleEnvironments ?? {}, pullRequestBaseBranch: p.pullRequestBaseBranch ?? '', hasGitHubToken: p.hasGitHubToken ?? false });
  }

  /** Editando um projeto e trocando a máquina dele: avisa o que acontece com o clone existente. */
  environmentChanged(): boolean {
    return this.editingId() !== null && (this.draft.environmentId ?? null) !== this.originalEnvironmentId;
  }

  /** Token digitado agora (nunca preenchido a partir do servidor) e se o guardado deve ser apagado. */
  tokenInput = '';
  tokenCleared = false;

  setToken(value: string): void {
    this.tokenInput = value;
    if (value) this.tokenCleared = false;
  }

  clearToken(): void {
    this.tokenInput = '';
    this.tokenCleared = true;
  }

  private openForm(id: string | null, draft: ProjectInput): void {
    this.tokenInput = '';
    this.tokenCleared = false;
    this.editingId.set(id);
    this.draft = draft;
    this.originalEnvironmentId = draft.environmentId ?? null;
    this.formOpen.set(true);
  }

  closeForm(): void {
    this.formOpen.set(false);
    this.editingId.set(null);
  }

  save(): void {
    this.saving.set(true);
    const id = this.editingId();
    // o perfil de stack roda em container num ambiente: sem ambiente não há perfil
    const body: ProjectInput = { ...this.draft, stackProfileId: this.draft.environmentId ? (this.draft.stackProfileId ?? null) : null };
    delete body.hasGitHubToken;
    // token: digitado = substitui; "apagar" = ""; senão fica de fora e o servidor mantém o guardado
    if (this.tokenInput.trim()) body.githubToken = this.tokenInput.trim();
    else if (this.tokenCleared) body.githubToken = '';
    const request = id ? this.api.updateProject(id, body) : this.api.createProject(body);
    request.subscribe({
      next: () => {
        this.saving.set(false);
        this.closeForm();
        this.snack.open('Projeto salvo.', 'OK', { duration: 3000 });
        this.load();
      },
      error: () => {
        this.saving.set(false);
        this.snack.open('Não foi possível salvar o projeto.', 'OK', { duration: 5000 });
      },
    });
  }

  remove(p: Project): void {
    this.dialog
      .open(ActionDialogComponent, {
        data: { title: 'Excluir projeto', message: `Excluir "${p.name}" e todas as suas demandas e tarefas?`, confirmLabel: 'Excluir' },
      })
      .afterClosed()
      .subscribe((ok) => {
        if (ok) this.api.deleteProject(p.id).subscribe(() => this.load());
      });
  }
}
