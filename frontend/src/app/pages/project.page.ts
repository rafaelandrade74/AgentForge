import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { ApiService } from '../core/api.service';
import {
  BOARD_STATES, DEMAND_LABEL, DEMAND_TONE, STATE_LABEL, STATE_TONE, groupByState,
} from '../core/labels';
import { Demand, Project, WorkItem } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { BadgeComponent } from '../shared/shared';

@Component({
  selector: 'app-project-page',
  standalone: true,
  imports: [RouterLink, MatButtonModule, BadgeComponent],
  template: `
    <div class="page">
      @if (project(); as p) {
        <div class="row">
          <div>
            <h1>{{ p.name }}</h1>
            <div class="muted mono">{{ p.repositoryUrl }} · base {{ p.baseBranch }}</div>
          </div>
          <span class="spacer"></span>
          <app-badge [label]="p.autoMode ? 'modo automático' : 'com aprovações humanas'" [tone]="p.autoMode ? 'warn' : 'ok'" />
          <a mat-flat-button [routerLink]="['demands', 'new']">Nova demanda</a>
        </div>

        <h2>Demandas</h2>
        <div class="demands">
          @for (d of demands(); track d.id) {
            <a class="demand" [routerLink]="['/demands', d.id]">
              <strong>{{ d.title }}</strong>
              <app-badge [label]="demandLabel[d.status]" [tone]="demandTone[d.status]" />
            </a>
          } @empty {
            <p class="muted">Nenhuma demanda. Crie a primeira em "Nova demanda".</p>
          }
        </div>

        <h2>Quadro</h2>
        <div class="board" role="list">
          @for (s of states; track s) {
            <section class="col" role="listitem" [attr.aria-label]="stateLabel[s]">
              <header>
                <span>{{ stateLabel[s] }}</span>
                <span class="count">{{ groups()[s].length }}</span>
              </header>
              @for (w of groups()[s]; track w.id) {
                <a class="card" [routerLink]="['/tasks', w.id]">
                  <div class="title">{{ w.title }}</div>
                  <div class="muted small">{{ demandTitle(w.demandId) }}</div>
                  <div class="row small">
                    @if (w.reworkCount > 0) {
                      <app-badge [label]="'retrabalho ' + w.reworkCount" tone="warn" />
                    }
                    @if (w.state === 'Blocked') {
                      <app-badge label="precisa de decisão" tone="bad" />
                    }
                    @if (w.state === 'AwaitingApproval') {
                      <app-badge label="aprovar entrega" tone="warn" />
                    }
                    @if (w.state === 'Backlog') {
                      <app-badge label="aguarda plano" [tone]="stateTone[w.state]" />
                    }
                  </div>
                </a>
              }
            </section>
          }
        </div>
        @if (cancelled().length > 0) {
          <p class="muted">{{ cancelled().length }} tarefa(s) cancelada(s) não aparecem no quadro.</p>
        }
      } @else {
        <p class="muted">Carregando…</p>
      }
    </div>
  `,
  styles: `
    .demands { display: flex; flex-direction: column; gap: .4rem; }
    .demand { display: flex; gap: .75rem; align-items: center; padding: .6rem .8rem; background: #1b1b1b; border-radius: 8px; text-decoration: none; color: inherit; }
    .demand:hover { background: #242424; }
    .board { display: flex; gap: .75rem; overflow-x: auto; padding-bottom: 1rem; align-items: flex-start; }
    .col { flex: 0 0 15.5rem; background: #181818; border-radius: 10px; padding: .5rem; min-height: 6rem; }
    .col header { display: flex; justify-content: space-between; font-weight: 600; padding: .25rem .4rem .5rem; }
    .count { background: #333; border-radius: 999px; padding: 0 .5rem; font-size: .8rem; }
    .card { display: block; background: #242424; border-radius: 8px; padding: .6rem .7rem; margin-bottom: .5rem; text-decoration: none; color: inherit; border: 1px solid transparent; }
    .card:hover, .card:focus-visible { border-color: var(--mat-sys-primary, #b69df8); outline: none; }
    .title { font-weight: 500; margin-bottom: .2rem; }
    .small { font-size: .8rem; margin-top: .3rem; }
  `,
})
export class ProjectPage {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);

  readonly id = input.required<string>();
  readonly project = signal<Project | null>(null);
  readonly demands = signal<Demand[]>([]);
  readonly items = signal<WorkItem[]>([]);

  readonly states = BOARD_STATES;
  readonly stateLabel = STATE_LABEL;
  readonly stateTone = STATE_TONE;
  readonly demandLabel = DEMAND_LABEL;
  readonly demandTone = DEMAND_TONE;
  readonly groups = computed(() => groupByState(this.items()));
  readonly cancelled = computed(() => this.items().filter((i) => i.state === 'Cancelled'));

  constructor() {
    this.realtime.bindProject(() => this.id());

    this.realtime.workItemChanged$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.projectId !== this.id()) return;
      const known = this.items().some((i) => i.id === e.workItemId);
      if (!known) return this.loadItems();
      this.items.update((list) =>
        list.map((i) => (i.id === e.workItemId
          ? { ...i, state: e.state, reworkCount: e.reworkCount, blockedReason: e.blockedReason, blockedFromState: e.blockedFromState, waitingReason: e.waitingReason, branchName: e.branchName }
          : i)));
    });
    this.realtime.demandChanged$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.projectId === this.id()) this.loadDemands();
    });
    this.realtime.reconnected$.pipe(takeUntilDestroyed()).subscribe(() => this.loadAll());

    effect(() => {
      this.id();
      untracked(() => this.loadAll());
    });
  }

  demandTitle(demandId: string): string {
    return this.demands().find((d) => d.id === demandId)?.title ?? '';
  }

  private loadAll(): void {
    const id = this.id();
    this.api.project(id).subscribe((p) => this.project.set(p));
    this.loadDemands();
    this.loadItems();
  }

  private loadDemands(): void {
    this.api.demands(this.id()).subscribe((d) => this.demands.set(d));
  }

  private loadItems(): void {
    this.api.workItemsOfProject(this.id()).subscribe((i) => this.items.set(i));
  }
}
