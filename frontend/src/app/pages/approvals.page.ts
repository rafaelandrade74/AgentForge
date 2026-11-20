import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
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
  imports: [RouterLink, BadgeComponent],
  template: `
    <div class="content-area">
      <div class="page-header">
        <div>
          <div class="page-title">Aprovações</div>
          <div class="page-subtitle">Tarefas aguardando revisão humana</div>
        </div>
        <div class="header-actions">
          <button class="btn btn-secondary" type="button" (click)="load()">Atualizar</button>
        </div>
      </div>

      @if (items() === null) {
        <p style="color:var(--text-2)">Carregando…</p>
      } @else if (items()!.length === 0) {
        <div class="card card-p" style="text-align:center;padding:2.5rem" data-testid="approvals-empty">
          <strong>Nada aguardando aprovação.</strong>
          <div style="color:var(--text-2);margin-top:4px">Quando um agente terminar uma tarefa, ela aparece aqui.</div>
        </div>
      } @else {
        @for (g of groups(); track g.projectId) {
          <div class="section-title">
            <a [routerLink]="['/projects', g.projectId]" style="color:inherit;text-decoration:none">{{ g.project }}</a>
            <span class="count">{{ g.items.length }}</span>
          </div>
          <div style="display:flex;flex-direction:column;gap:8px;margin-bottom:24px" data-testid="approvals-project">
            @for (a of g.items; track a.id) {
              <div class="card card-p" style="cursor:pointer" data-testid="approval-item">
                <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap">
                  <span class="badge badge-warn">Aprovação</span>
                  <div>
                    <a [routerLink]="['/tasks', a.id]" style="font-size:14px;font-weight:500;color:var(--text-0);text-decoration:none">{{ a.title }}</a>
                    <div style="font-size:12px;color:var(--text-2)">{{ a.demand }}@if (a.branchName) { · <span style="font-family:var(--mono)">{{ a.branchName }}</span> } · {{ waited(a) }}</div>
                  </div>
                  <div style="margin-left:auto;display:flex;gap:6px">
                    @if (a.reworkCount > 0) { <span class="badge badge-warn">retrabalho {{ a.reworkCount }}</span> }
                    <a [routerLink]="['/tasks', a.id]" class="btn btn-success btn-sm">Aprovar</a>
                    <a [routerLink]="['/tasks', a.id]" class="btn btn-danger btn-sm">Rejeitar</a>
                  </div>
                </div>
              </div>
            }
          </div>
        }
      }
    </div>
  `,
  styles: `
    .content-area { padding: 24px; max-width: 1200px; }
    @media (max-width: 639px) { .content-area { padding: 16px; } }
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
