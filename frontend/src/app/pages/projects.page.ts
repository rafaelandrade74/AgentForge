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
  imports: [FormsModule, RouterLink, MatButtonModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, MatSelectModule, BadgeComponent, GithubImportComponent, NgTemplateOutlet],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <div class="page-title">Projetos</div>
          <div class="page-subtitle">Repositórios e demandas em andamento</div>
        </div>
        <div class="header-actions">
          <button class="btn btn-secondary" type="button" (click)="toggleGroupForm()">Novo grupo</button>
          <button class="btn btn-secondary" type="button" (click)="importOpen.set(!importOpen())">Importar do GitHub</button>
          <button class="btn btn-primary" type="button" (click)="toggleForm()">{{ formOpen() ? 'Fechar' : 'Novo projeto' }}</button>
        </div>
      </div>

      @if (importOpen()) {
        <app-github-import [environments]="environments()" (imported)="onImported($event)" (closed)="importOpen.set(false)" />
      }

      @if (formOpen()) {
        <div class="card card-p form-card">
          <div class="section-title">{{ editingId() ? 'Editar projeto' : 'Cadastrar projeto' }}</div>
          <form (ngSubmit)="save()" #f="ngForm" class="form-grid">
            <div class="form-group"><div class="form-label">Nome</div><input class="form-input" name="name" [(ngModel)]="draft.name" required /></div>
            <div class="form-group"><div class="form-label">URL do repositório Git</div><input class="form-input" name="repositoryUrl" [(ngModel)]="draft.repositoryUrl" required /></div>
            <div class="form-group"><div class="form-label">Branch base</div><input class="form-input" name="baseBranch" [(ngModel)]="draft.baseBranch" /></div>
            <div class="form-group"><div class="form-label">Comando de build</div><input class="form-input" name="buildCommand" [(ngModel)]="draft.buildCommand" placeholder="dotnet build" /></div>
            <div class="form-group"><div class="form-label">Comando de teste</div><input class="form-input" name="testCommand" [(ngModel)]="draft.testCommand" placeholder="dotnet test" /></div>
            @if (groups().length > 0) {
              <div class="form-group"><div class="form-label">Grupo</div>
                <select class="form-input" name="projectGroupId" [(ngModel)]="draft.projectGroupId">
                  <option [ngValue]="null">Sem grupo</option>
                  @for (g of groups(); track g.id) { <option [ngValue]="g.id">{{ g.name }}</option> }
                </select>
              </div>
            }
            <div class="form-group"><div class="form-label">Ambiente de execução</div>
              <select class="form-input" name="environmentId" [(ngModel)]="draft.environmentId">
                <option [ngValue]="null">Padrão do servidor</option>
                @for (e of environments(); track e.id) { <option [ngValue]="e.id">{{ e.name }}</option> }
              </select>
            </div>
            @if (draft.environmentId) {
              <div class="form-group"><div class="form-label">Perfil de stack (container)</div>
                <select class="form-input" name="stackProfileId" [(ngModel)]="draft.stackProfileId">
                  <option [ngValue]="null">Nenhum (roda direto na máquina)</option>
                  @for (s of profiles(); track s.id) { <option [ngValue]="s.id">{{ s.name }}</option> }
                </select>
              </div>
            }
            <div class="form-group"><div class="form-label">Branch de destino dos PRs</div>
              <input class="form-input" name="pullRequestBaseBranch" [(ngModel)]="draft.pullRequestBaseBranch" placeholder="develop (vazio = a branch base)" data-testid="pr-branch" />
            </div>
            <div class="form-group full" data-testid="gh-token">
              <div class="form-label">{{ draft.hasGitHubToken && !tokenCleared ? 'Token do GitHub para PRs (vazio mantém o guardado)' : 'Token do GitHub para PRs' }}</div>
              <input class="form-input" type="password" name="githubToken" autocomplete="off" [ngModel]="tokenInput" (ngModelChange)="setToken($event)"
                placeholder="github_pat_… (Contents e Pull requests de escrita, só neste repositório)" />
              @if (draft.hasGitHubToken && !tokenCleared) {
                <p style="font-size:12px;color:var(--text-2);margin-top:4px">Há um token guardado (criptografado; nunca volta para esta tela).
                  <button class="btn btn-secondary btn-sm" type="button" (click)="clearToken()" style="margin-left:6px">Apagar token</button></p>
              } @else if (tokenCleared) {
                <p style="font-size:12px;color:var(--text-2);margin-top:4px">O token guardado será apagado ao salvar.</p>
              }
            </div>
            @if (environments().length > 0) {
              <div class="form-group full" data-testid="role-envs">
                <p style="font-size:12px;color:var(--text-2);margin-bottom:8px">Revisão em outra máquina (opcional): Tech Lead e Security recebem só o diff, sem o repositório. Dev e QA ficam sempre no ambiente do projeto.</p>
                <div class="form-grid">
                  @for (role of movableRoles; track role.key) {
                    <div class="form-group"><div class="form-label">{{ role.label }} roda em</div>
                      <select class="form-input" [name]="'role-' + role.key" [ngModel]="roleEnv(role.key)" (ngModelChange)="setRoleEnv(role.key, $event)">
                        <option [ngValue]="null">Mesmo ambiente do projeto</option>
                        @for (e of environments(); track e.id) { <option [ngValue]="e.id">{{ e.name }}</option> }
                      </select>
                    </div>
                  }
                </div>
              </div>
            }
            @if (environmentChanged()) {
              <p class="form-group full" style="font-size:12px;color:var(--text-2)" data-testid="env-change-hint">
                O clone atual continua na máquina anterior (não é movido): o próximo uso clona o repositório na nova máquina, que precisa
                ter acesso a ele — gere a chave dela na tela Git e autorize-a no GitHub.
              </p>
            }
            <div class="form-group full">
              <mat-checkbox name="autoMode" [(ngModel)]="draft.autoMode">Modo automático (sem aprovações humanas)</mat-checkbox>
            </div>
            <div class="form-group full" style="display:flex;gap:8px">
              <button class="btn btn-primary" type="submit" [disabled]="f.invalid || saving()">Salvar</button>
              <button class="btn btn-secondary" type="button" (click)="closeForm()">Cancelar</button>
            </div>
          </form>
        </div>
      }

      @if (groupFormOpen()) {
        <div class="card card-p form-card">
          <div class="section-title">{{ editingGroupId() ? 'Editar grupo' : 'Novo grupo' }}</div>
          <form (ngSubmit)="saveGroup()" class="form-grid" style="margin-top:8px">
            <div class="form-group"><div class="form-label">Nome</div><input class="form-input" [(ngModel)]="groupDraft.name" name="gname" required /></div>
            <div class="form-group"><div class="form-label">Descrição</div><input class="form-input" [(ngModel)]="groupDraft.description" name="gdesc" /></div>
            <div class="form-group full" style="display:flex;gap:8px">
              <button class="btn btn-primary" type="submit" [disabled]="!groupDraft.name">Salvar</button>
              <button class="btn btn-secondary" type="button" (click)="groupFormOpen.set(false)">Cancelar</button>
            </div>
          </form>
        </div>
      }

      @for (g of groups(); track g.id) {
        <section class="group-section">
          <div class="section-title" style="display:flex;align-items:center;gap:8px">
            <a [routerLink]="['/groups', g.id]" style="text-decoration:none;color:inherit;font-size:14px">{{ g.name }}</a>
            <span class="count">{{ projectsOfGroup(g.id).length }}</span>
            @if (g.description) { <span style="font-size:12px;color:var(--text-2);font-weight:400">{{ g.description }}</span> }
            <span style="flex:1"></span>
            <button class="btn btn-secondary btn-sm" type="button" (click)="editGroup(g)">Editar</button>
            <button class="btn btn-danger btn-sm" type="button" (click)="removeGroup(g)">Excluir</button>
          </div>
          <div class="grid-auto">
            @for (p of projectsOfGroup(g.id); track p.id) {
              <ng-container *ngTemplateOutlet="projectCard; context: { $implicit: p }"></ng-container>
            } @empty {
              <p style="font-size:13px;color:var(--text-2)">Nenhum projeto neste grupo.</p>
            }
          </div>
        </section>
      }

      @if (ungroupedProjects().length > 0 || (groups().length === 0 && loaded())) {
        @if (groups().length > 0) { <div class="section-title">Sem grupo <span class="count">{{ ungroupedProjects().length }}</span></div> }
        <div class="grid-auto">
          @for (p of ungroupedProjects(); track p.id) {
            <ng-container *ngTemplateOutlet="projectCard; context: { $implicit: p }"></ng-container>
          } @empty {
            @if (loaded() && groups().length === 0) {
              <p style="font-size:13px;color:var(--text-2)">Nenhum projeto ainda. Cadastre o primeiro para começar.</p>
            }
          }
        </div>
      }

      <ng-template #projectCard let-p>
        <div class="project-card" [routerLink]="['/projects', p.id]">
          <div style="display:flex;align-items:center;gap:10px">
            <div class="p-icon violet">
              <svg width="18" height="18" viewBox="0 0 18 18"><path d="M3 5l6-3 6 3v7l-6 3-6-3V5z" fill="currentColor"/></svg>
            </div>
            <div>
              <div style="font-size:14px;font-weight:600">{{ p.name }}</div>
              <div style="font-size:11.5px;color:var(--text-2);font-family:var(--mono)">{{ p.repositoryUrl }}</div>
            </div>
          </div>
          <div style="display:flex;gap:6px;flex-wrap:wrap">
            <span class="badge badge-idle">{{ p.baseBranch }}</span>
            @if (p.autoMode) { <span class="badge badge-accent">Auto</span> }
            @if (environmentName(p); as env) { <span class="badge badge-info">{{ env }}</span> }
            @if (profileName(p); as prof) { <span class="badge badge-info">{{ prof }}</span> }
          </div>
          <div style="display:flex;gap:16px;font-size:12px;color:var(--text-2)">
            <span><b style="color:var(--text-0)">{{ p.demandCount ?? 0 }}</b> demandas</span>
            <span><b style="color:var(--text-0)">{{ p.taskCount ?? 0 }}</b> tarefas</span>
            <span><b style="color:var(--success)">{{ p.doneCount ?? 0 }}</b> done</span>
          </div>
          <div class="progress-bar"><div class="progress-fill" [style.width.%]="p.taskCount ? ((p.doneCount ?? 0) / p.taskCount * 100) : 0"></div></div>
          <div style="display:flex;gap:6px;margin-top:4px" (click)="$event.stopPropagation()">
            <a class="btn btn-secondary btn-sm" [routerLink]="['/projects', p.id, 'demands', 'new']">Nova demanda</a>
            <button class="btn btn-secondary btn-sm" type="button" (click)="edit(p)">Editar</button>
            <button class="btn btn-danger btn-sm" type="button" (click)="remove(p)">Excluir</button>
          </div>
        </div>
      </ng-template>
    </div>
  `,
  styles: `
    .form-card { margin: 16px 0; }
    .group-section { margin-top: 24px; }
    @media (max-width: 639px) { :host ::ng-deep .grid-auto { grid-template-columns: 1fr; } }
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
