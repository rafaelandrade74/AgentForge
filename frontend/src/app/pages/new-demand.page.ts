import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';

@Component({
  selector: 'app-new-demand-page',
  standalone: true,
  imports: [FormsModule, RouterLink, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <div class="page narrow">
      <a [routerLink]="['/projects', id()]">← Voltar ao projeto</a>
      <h1>Nova demanda</h1>
      <p class="muted">
        Descreva o que precisa ser feito em linguagem natural. O Scrum Master vai ler o repositório e quebrar a demanda em tarefas.
      </p>
      <form (ngSubmit)="submit(false)" #f="ngForm">
        <mat-form-field appearance="outline" class="full">
          <mat-label>Título</mat-label>
          <input matInput name="title" [(ngModel)]="title" required maxlength="300" />
        </mat-form-field>
        <mat-form-field appearance="outline" class="full">
          <mat-label>Descrição</mat-label>
          <textarea matInput name="description" [(ngModel)]="description" rows="10"
            placeholder="Ex.: adicionar filtro por status no endpoint de listagem de tarefas"></textarea>
        </mat-form-field>
        <div class="row">
          <button mat-flat-button type="button" [disabled]="f.invalid || busy()" (click)="submit(true)">Criar e iniciar refinamento</button>
          <button mat-stroked-button type="submit" [disabled]="f.invalid || busy()">Só criar</button>
        </div>
      </form>
    </div>
  `,
  styles: `
    .narrow { max-width: 46rem; }
    .full { width: 100%; }
  `,
})
export class NewDemandPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly snack = inject(MatSnackBar);

  readonly id = input.required<string>();
  readonly busy = signal(false);
  title = '';
  description = '';

  submit(start: boolean): void {
    this.busy.set(true);
    this.api.createDemand({ projectId: this.id(), title: this.title.trim(), description: this.description }).subscribe({
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
