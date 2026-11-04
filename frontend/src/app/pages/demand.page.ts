import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { DEMAND_LABEL, DEMAND_TONE, STATE_LABEL, STATE_TONE, formatUsd } from '../core/labels';
import { Demand, Project, RunSummary, WorkItem } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { BadgeComponent, LiveLine, LiveLogComponent, RunTimelineComponent, appendLine } from '../shared/shared';
import { MarkdownComponent } from '../shared/markdown.component';

@Component({
  selector: 'app-demand-page',
  standalone: true,
  imports: [RouterLink, MatButtonModule, BadgeComponent, LiveLogComponent, MarkdownComponent, RunTimelineComponent],
  template: `
    <div class="page">
      @if (demand(); as d) {
        <a [routerLink]="['/projects', d.projectId]">← {{ project()?.name ?? 'Projeto' }}</a>
        <div class="row">
          <h1>{{ d.title }}</h1>
          <app-badge [label]="demandLabel[d.status]" [tone]="demandTone[d.status]" />
          <span class="spacer"></span>
          @if (d.status === 'New' || d.status === 'Failed') {
            <button mat-flat-button type="button" (click)="start()">
              {{ d.status === 'Failed' ? 'Reexecutar refinamento' : 'Iniciar refinamento' }}
            </button>
          }
          @if (backlogCount() > 0) {
            <button mat-flat-button type="button" (click)="approvePlan()">Aprovar plano ({{ backlogCount() }})</button>
          }
        </div>
        @if (d.description) {
          <div class="desc"><app-markdown [source]="d.description" /></div>
        }
        @if (d.status === 'Failed') {
          <p class="bad-text">O Scrum Master não conseguiu produzir um plano válido. Veja a saída nas execuções abaixo e reexecute.</p>
        }

        <h2>Plano ({{ items().length }} tarefas)</h2>
        @if (items().length > 0 && backlogCount() > 0) {
          <p class="muted">O plano aguarda a sua aprovação para ser liberado ao time.</p>
        }
        <div class="items">
          @for (w of items(); track w.id) {
            <a class="item" [routerLink]="['/tasks', w.id]">
              <div class="row">
                <strong>{{ w.title }}</strong>
                <app-badge [label]="stateLabel[w.state]" [tone]="stateTone[w.state]" />
                <span class="spacer"></span>
                @if (w.estimate) { <span class="muted">estimativa {{ w.estimate }}</span> }
              </div>
              @if (w.dependsOnIds.length > 0) {
                <div class="muted small">depende de: {{ depTitles(w) }}</div>
              }
              @if (w.acceptanceCriteria) {
                <div class="crit"><app-markdown [source]="w.acceptanceCriteria" [links]="false" /></div>
              }
            </a>
          } @empty {
            <p class="muted">Nenhuma tarefa ainda{{ d.status === 'New' ? ' — inicie o refinamento.' : '.' }}</p>
          }
        </div>

        <h2>Scrum Master — ao vivo</h2>
        <app-live-log [lines]="live()" empty="Aguardando atividade do Scrum Master." />

        <h2>Execuções do refinamento <span class="muted small">· {{ usd(cost()) }}</span></h2>
        <app-run-timeline [runs]="runs()" />
      } @else {
        <p class="muted">Carregando…</p>
      }
    </div>
  `,
  styles: `
    .desc { max-width: 60rem; }
    .items { display: flex; flex-direction: column; gap: .5rem; }
    .item { display: block; background: #1b1b1b; border-radius: 8px; padding: .6rem .8rem; text-decoration: none; color: inherit; }
    .item:hover { background: #242424; }
    .small { font-size: .8rem; }
    .crit { margin: .4rem 0 0; font-size: .85rem; color: #aaa; }
    .bad-text { color: #ffb4b4; }
  `,
})
export class DemandPage {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly snack = inject(MatSnackBar);

  readonly id = input.required<string>();
  readonly demand = signal<Demand | null>(null);
  readonly project = signal<Project | null>(null);
  readonly items = signal<WorkItem[]>([]);
  readonly runs = signal<RunSummary[]>([]);
  readonly live = signal<LiveLine[]>([]);

  readonly demandLabel = DEMAND_LABEL;
  readonly demandTone = DEMAND_TONE;
  readonly stateLabel = STATE_LABEL;
  readonly stateTone = STATE_TONE;
  readonly usd = formatUsd;
  readonly backlogCount = computed(() => this.items().filter((i) => i.state === 'Backlog').length);
  /** Só as execuções do refinamento (as das tarefas aparecem na tela de cada tarefa). */
  readonly cost = computed(() => this.runs().reduce((sum, r) => sum + r.costUsd, 0));

  constructor() {
    this.realtime.bindProject(() => this.demand()?.projectId ?? null);

    this.realtime.runEvent$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.demandId !== this.id() || e.workItemId !== null) return;
      this.live.update((l) => appendLine(l, e));
      if (e.kind === 'run_started' || e.kind === 'run_finished') this.loadRuns();
    });
    this.realtime.demandChanged$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.demandId !== this.id()) return;
      this.demand.update((d) => (d ? { ...d, status: e.status } : d));
      this.loadItems();
    });
    this.realtime.workItemChanged$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.demandId !== this.id()) return;
      if (!this.items().some((i) => i.id === e.workItemId)) return this.loadItems();
      this.items.update((list) => list.map((i) => (i.id === e.workItemId ? { ...i, state: e.state } : i)));
    });
    this.realtime.reconnected$.pipe(takeUntilDestroyed()).subscribe(() => this.loadAll());

    effect(() => {
      this.id();
      untracked(() => this.loadAll());
    });
  }

  depTitles(w: WorkItem): string {
    return w.dependsOnIds.map((id) => this.items().find((i) => i.id === id)?.title ?? '?').join(', ');
  }

  start(): void {
    this.api.startDemand(this.id()).subscribe({
      error: () => this.snack.open('Não foi possível iniciar o refinamento.', 'OK', { duration: 5000 }),
    });
  }

  approvePlan(): void {
    this.api.approvePlan(this.id()).subscribe({
      next: () => this.snack.open('Plano aprovado: as tarefas foram liberadas.', 'OK', { duration: 4000 }),
      error: () => this.snack.open('Não foi possível aprovar o plano.', 'OK', { duration: 5000 }),
    });
  }

  private loadAll(): void {
    this.api.demand(this.id()).subscribe((d) => {
      this.demand.set(d);
      this.api.project(d.projectId).subscribe((p) => this.project.set(p));
    });
    this.loadItems();
    this.loadRuns();
  }

  private loadItems(): void {
    this.api.workItemsOfDemand(this.id()).subscribe((i) => this.items.set(i));
  }

  private loadRuns(): void {
    this.api.runsOfDemand(this.id()).subscribe((r) => this.runs.set(r.filter((x) => x.workItemId === null)));
  }
}
