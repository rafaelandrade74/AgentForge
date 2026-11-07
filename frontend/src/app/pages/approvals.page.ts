import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { debounceTime, interval } from 'rxjs';
import { ApiService } from '../core/api.service';
import { Approval } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { BadgeComponent } from '../shared/shared';

/** "há 5 min", "há 3 h", "há 2 dias": quanto tempo a entrega espera a aprovação. */
export function waitingFor(iso: string | null, now: Date = new Date()): string {
  if (!iso) return '';
  const minutes = Math.max(0, Math.round((now.getTime() - new Date(iso).getTime()) / 60000));
  if (minutes < 1) return 'agora há pouco';
  if (minutes < 60) return `há ${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 48) return `há ${hours} h`;
  return `há ${Math.round(hours / 24)} dias`;
}

@Component({
  selector: 'app-approvals-page',
  standalone: true,
  imports: [RouterLink, MatButtonModule, BadgeComponent],
  template: `
    <div class="page">
      <div class="row">
        <h1>Aguardando aprovação</h1>
        <span class="spacer"></span>
        <button mat-button type="button" (click)="load()">Atualizar</button>
      </div>
      <p class="muted">
        Entregas prontas de <strong>todos os projetos</strong> que esperam a sua decisão. Abra a tarefa para ver o diff e
        <strong>aprovar</strong> ou <strong>rejeitar com comentário</strong>. As mais antigas aparecem primeiro.
      </p>

      @if (items() === null) {
        <p class="muted">Carregando…</p>
      } @else if (items()!.length === 0) {
        <section class="card empty" data-testid="approvals-empty">
          <strong>Nada aguardando aprovação.</strong>
          <span class="muted">Quando um agente terminar uma tarefa, ela aparece aqui.</span>
        </section>
      } @else {
        <p class="muted small" data-testid="approvals-total">{{ items()!.length }} tarefa(s) em {{ groups().length }} projeto(s).</p>
        @for (g of groups(); track g.projectId) {
          <section class="card" data-testid="approvals-project">
            <div class="row">
              <h2><a [routerLink]="['/projects', g.projectId]">{{ g.project }}</a></h2>
              <app-badge [label]="g.items.length + ' aguardando'" tone="warn" />
            </div>
            @for (a of g.items; track a.id) {
              <a class="item" [routerLink]="['/tasks', a.id]" data-testid="approval-item">
                <div class="main">
                  <div class="title">{{ a.title }}</div>
                  <div class="muted small">{{ a.demand }}@if (a.branchName) { · <span class="mono">{{ a.branchName }}</span> }</div>
                </div>
                <div class="meta">
                  @if (a.reworkCount > 0) { <app-badge [label]="'retrabalho ' + a.reworkCount" tone="warn" /> }
                  <span class="muted small">{{ waited(a) }}</span>
                  <span class="go">Revisar →</span>
                </div>
              </a>
            }
          </section>
        }
      }
    </div>
  `,
  styles: `
    .card { margin-top: 1rem; padding: 1rem; background: #151515; border: 1px solid #2a2a2a; border-radius: 10px; }
    .card.empty { display: flex; flex-direction: column; gap: .25rem; }
    .card h2 { margin: 0; font-size: 1.1rem; }
    .card h2 a { color: inherit; text-decoration: none; }
    .item { display: flex; gap: 1rem; align-items: center; justify-content: space-between; flex-wrap: wrap; padding: .6rem .75rem; margin-top: .5rem; border: 1px solid #2a2a2a; border-radius: 8px; background: #1b1b1b; color: inherit; text-decoration: none; }
    .item:hover { border-color: #c9a227; }
    .title { font-weight: 600; }
    .meta { display: flex; gap: .75rem; align-items: center; }
    .go { color: #c9a227; font-weight: 600; }
  `,
})
export class ApprovalsPage {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);

  readonly items = signal<Approval[] | null>(null);

  /** Agrupa por projeto; os projetos com a entrega mais antiga primeiro, e dentro de cada um também. */
  readonly groups = computed(() => {
    const byProject = new Map<string, { projectId: string; project: string; items: Approval[] }>();
    for (const a of this.items() ?? []) {
      const g = byProject.get(a.projectId) ?? { projectId: a.projectId, project: a.project, items: [] };
      g.items.push(a);
      byProject.set(a.projectId, g);
    }
    return [...byProject.values()];
  });

  constructor() {
    this.load();
    this.realtime.workItemChanged$.pipe(debounceTime(500), takeUntilDestroyed()).subscribe(() => this.load());
    interval(60_000).pipe(takeUntilDestroyed()).subscribe(() => this.load());
  }

  load(): void {
    this.api.approvals().subscribe({ next: (a) => this.items.set(a), error: () => undefined });
  }

  waited(a: Approval): string {
    return 'aguardando ' + waitingFor(a.waitingSince ?? a.createdAt);
  }
}
