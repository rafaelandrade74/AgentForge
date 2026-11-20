import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';

@Component({
  selector: 'app-new-demand-page',
  standalone: true,
  imports: [FormsModule, RouterLink],
  template: `
    <div class="content-area" style="max-width:46rem">
      <nav class="breadcrumb"><a [routerLink]="['/projects', id()]">Projeto</a> <span class="sep">/</span> <span>Nova demanda</span></nav>
      <div class="page-header">
        <div>
          <div class="page-title">Nova demanda</div>
          <div class="page-subtitle">Descreva o que precisa ser feito em linguagem natural. O Scrum Master vai ler o repositório e quebrar a demanda em tarefas.</div>
        </div>
      </div>

      <div class="card card-p">
        <form (ngSubmit)="submit(false)" #f="ngForm" style="display:flex;flex-direction:column;gap:14px">
          <div class="form-group">
            <div class="form-label">Título *</div>
            <input class="form-input" style="width:100%" name="title" [(ngModel)]="title" required maxlength="300" placeholder="Ex.: Adicionar filtro por status na listagem" />
          </div>
          <div class="form-group">
            <div class="form-label">Descrição</div>
            <textarea class="form-input" style="width:100%;min-height:180px;resize:vertical" name="description" [(ngModel)]="description"
              placeholder="Descreva em linguagem natural o que precisa ser feito…"></textarea>
          </div>
          <div style="display:flex;gap:10px;justify-content:flex-end;padding-top:8px;border-top:1px solid var(--border)">
            <button class="btn btn-secondary" type="submit" [disabled]="f.invalid || busy()">Só criar</button>
            <button class="btn btn-primary" type="button" [disabled]="f.invalid || busy()" (click)="submit(true)">Criar e iniciar refinamento</button>
          </div>
        </form>
      </div>
    </div>
  `,
  styles: `
    .breadcrumb { font-size: 13px; color: var(--text-2); margin-bottom: .25rem; }
    .breadcrumb a { color: var(--text-2); text-decoration: none; }
    .breadcrumb a:hover { color: var(--accent); }
    .breadcrumb .sep { margin: 0 .35rem; opacity: .5; }
    .breadcrumb span:last-child { color: var(--text-0); font-weight: 500; }
  `,
})
export class NewDemandPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly snack = inject(MatSnackBar);

  readonly id = input.required<string>();
  readonly busy = signal(false);
  title = '';
  description = '';

  private get groupId(): string | null { return this.route.snapshot.queryParamMap.get('projectGroupId'); }

  submit(start: boolean): void {
    this.busy.set(true);
    const body = this.groupId
      ? { projectId: null as any, projectGroupId: this.groupId, title: this.title.trim(), description: this.description }
      : { projectId: this.id(), title: this.title.trim(), description: this.description };
    this.api.createDemand(body).subscribe({
      next: (demand) => {
        const go = () => this.router.navigate(['/demands', demand.id]);
        if (!start) return void go();
        this.api.startDemand(demand.id).subscribe({ next: go, error: go });
      },
      error: () => {
        this.busy.set(false);
        this.snack.open('Não foi possível criar a demanda.', 'OK', { duration: 5000 });
      },
    });
  }
}
