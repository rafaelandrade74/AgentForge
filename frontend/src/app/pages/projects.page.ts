import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { Project, ProjectInput } from '../core/models';
import { ActionDialogComponent, BadgeComponent } from '../shared/shared';

const EMPTY: ProjectInput = { name: '', repositoryUrl: '', baseBranch: 'main', buildCommand: '', testCommand: '', autoMode: false };

@Component({
  selector: 'app-projects-page',
  standalone: true,
  imports: [FormsModule, RouterLink, MatButtonModule, MatCardModule, MatCheckboxModule, MatFormFieldModule, MatInputModule, BadgeComponent],
  template: `
    <div class="page">
      <div class="row">
        <h1>Projetos</h1>
        <span class="spacer"></span>
        <button mat-flat-button type="button" (click)="toggleForm()">{{ formOpen() ? 'Fechar' : 'Novo projeto' }}</button>
      </div>

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

      <div class="cards">
        @for (p of projects(); track p.id) {
          <mat-card>
            <mat-card-header>
              <mat-card-title><a [routerLink]="['/projects', p.id]">{{ p.name }}</a></mat-card-title>
              <mat-card-subtitle class="mono">{{ p.repositoryUrl }}</mat-card-subtitle>
            </mat-card-header>
            <mat-card-content>
              <div class="row">
                <app-badge [label]="'base: ' + p.baseBranch" />
                <app-badge [label]="p.autoMode ? 'automático' : 'com aprovações'" [tone]="p.autoMode ? 'warn' : 'ok'" />
              </div>
            </mat-card-content>
            <mat-card-actions>
              <a mat-button [routerLink]="['/projects', p.id]">Abrir</a>
              <a mat-button [routerLink]="['/projects', p.id, 'demands', 'new']">Nova demanda</a>
              <button mat-button type="button" (click)="edit(p)">Editar</button>
              <button mat-button type="button" (click)="remove(p)">Excluir</button>
            </mat-card-actions>
          </mat-card>
        } @empty {
          @if (loaded()) {
            <p class="muted">Nenhum projeto ainda. Cadastre o primeiro para começar.</p>
          }
        }
      </div>
    </div>
  `,
  styles: `
    .form { margin: 1rem 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(18rem, 1fr)); gap: .75rem 1rem; padding-top: 1rem; }
    .grid mat-checkbox, .grid .row { grid-column: 1 / -1; }
    .cards { display: grid; grid-template-columns: repeat(auto-fill, minmax(22rem, 1fr)); gap: 1rem; margin-top: 1rem; }
    mat-card-title a { text-decoration: none; color: inherit; }
  `,
})
export class ProjectsPage {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly projects = signal<Project[]>([]);
  readonly loaded = signal(false);
  readonly formOpen = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  draft: ProjectInput = { ...EMPTY };

  constructor() {
    this.load();
  }

  private load(): void {
    this.api.projects().subscribe((p) => {
      this.projects.set(p);
      this.loaded.set(true);
    });
  }

  toggleForm(): void {
    if (this.formOpen()) this.closeForm();
    else this.openForm(null, { ...EMPTY });
  }

  edit(p: Project): void {
    this.openForm(p.id, { name: p.name, repositoryUrl: p.repositoryUrl, baseBranch: p.baseBranch, buildCommand: p.buildCommand, testCommand: p.testCommand, autoMode: p.autoMode });
  }

  private openForm(id: string | null, draft: ProjectInput): void {
    this.editingId.set(id);
    this.draft = draft;
    this.formOpen.set(true);
  }

  closeForm(): void {
    this.formOpen.set(false);
    this.editingId.set(null);
  }

  save(): void {
    this.saving.set(true);
    const id = this.editingId();
    const request = id ? this.api.updateProject(id, this.draft) : this.api.createProject(this.draft);
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
