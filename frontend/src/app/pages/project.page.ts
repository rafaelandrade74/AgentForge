import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import {
  BOARD_STATES, DEMAND_LABEL, DEMAND_TONE, STATE_LABEL, STATE_TONE, groupByState,
} from '../core/labels';
import { Demand, ExecutionEnvironment, MigrationResult, Project, WorkItem } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { BadgeComponent } from '../shared/shared';

@Component({
  selector: 'app-project-page',
  standalone: true,
  imports: [RouterLink, FormsModule, MatButtonModule, MatFormFieldModule, MatSelectModule, BadgeComponent],
  template: `
    <div class="page">
      @if (project(); as p) {
        <nav class="breadcrumb"><a routerLink="/">Projetos</a> <span class="sep">/</span> <span>{{ p.name }}</span></nav>
        <div class="row">
          <div>
            <h1>{{ p.name }}</h1>
            <div class="muted mono small">{{ p.repositoryUrl }} · base {{ p.baseBranch }}</div>
          </div>
          <span class="spacer"></span>
          <app-badge [label]="p.autoMode ? 'modo automático' : 'com aprovações humanas'" [tone]="p.autoMode ? 'warn' : 'ok'" />
          @if (environments().length > 0) {
            <button class="btn btn-secondary" type="button" (click)="toggleMigrate()">Migrar ambiente</button>
          }
          <a class="btn btn-primary" [routerLink]="['demands', 'new']">Nova demanda</a>
        </div>

        @if (showMigrate()) {
          <div class="migrate-panel card-surface">
            <h3>Migrar para outro ambiente</h3>
            <p class="muted small">Todos os branches ativos serão publicados no origin e o projeto passará a rodar no novo ambiente.</p>
            @if (migrateResult(); as r) {
              <div class="migrate-result">
                <span class="badge ok">{{ r.branchesPushed }} branch(es) transferido(s)</span>
                @if (r.branchesSkipped > 0) { <span class="badge warn">{{ r.branchesSkipped }} ignorado(s)</span> }
                @for (w of r.warnings; track w) { <div class="muted small">⚠ {{ w }}</div> }
              </div>
              <button mat-button type="button" (click)="showMigrate.set(false)" style="margin-top:.5rem">Fechar</button>
            } @else {
              <div class="row">
                <mat-form-field appearance="outline">
                  <mat-label>Ambiente de destino</mat-label>
                  <mat-select [(ngModel)]="migrateTarget">
                    <mat-option [value]="null">Padrão do servidor</mat-option>
                    @for (e of environments(); track e.id) {
                      <mat-option [value]="e.id" [disabled]="e.id === project()?.environmentId">{{ e.name }}</mat-option>
                    }
                  </mat-select>
                </mat-form-field>
                <button mat-flat-button type="button" [disabled]="migrating() || migrateTarget === project()?.environmentId" (click)="migrate()">
                  @if (migrating()) { Migrando… } @else { Confirmar migração }
                </button>
                <button mat-button type="button" (click)="showMigrate.set(false)">Cancelar</button>
              </div>
            }
          </div>
        }

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
    .breadcrumb { font-size: 13px; color: var(--text-2); margin-bottom: .25rem; }
    .breadcrumb a { color: var(--text-2); text-decoration: none; }
    .breadcrumb a:hover { color: var(--accent); }
    .breadcrumb .sep { margin: 0 .35rem; opacity: .5; }
    .breadcrumb span:last-child { color: var(--text-0); font-weight: 500; }
    h2 { padding-bottom: .4rem; border-bottom: 1px solid var(--border); }
    .demands { display: flex; flex-direction: column; gap: .5rem; }
    .demand { display: flex; gap: .75rem; align-items: center; padding: .75rem 1rem; background: var(--bg-1); border: 1px solid var(--border); border-left: 3px solid var(--accent); border-radius: var(--radius); text-decoration: none; color: inherit; transition: background .15s, border-color .15s; }
    .demand:hover { background: var(--bg-hover); border-color: var(--border-active); }
    .board { display: flex; gap: .75rem; overflow-x: auto; padding-bottom: 1rem; align-items: flex-start; }
    .col { flex: 0 0 15.5rem; background: var(--bg-1); border: 1px solid var(--border); border-radius: var(--radius); padding: .5rem; min-height: 6rem; border-top: 3px solid var(--border); }
    .col[aria-label="Backlog"] { border-top-color: var(--text-2); }
    .col[aria-label="Em desenvolvimento"], .col[aria-label="InDevelopment"] { border-top-color: var(--info); }
    .col[aria-label="Revisão Tech Lead"], .col[aria-label="Revisão Security"], .col[aria-label="QA"] { border-top-color: var(--warning); }
    .col[aria-label="Aguardando aprovação"] { border-top-color: var(--warning); }
    .col[aria-label="Done"], .col[aria-label="Concluída"] { border-top-color: var(--success); }
    .col[aria-label="Blocked"], .col[aria-label="Bloqueada"] { border-top-color: var(--danger); }
    .col header { display: flex; justify-content: space-between; font-weight: 600; padding: .25rem .4rem .5rem; font-size: 13px; }
    .count { background: var(--bg-3); border-radius: 999px; padding: 0 .5rem; font-size: 12px; }
    .card { display: block; background: var(--bg-2); border: 1px solid var(--border); border-left: 3px solid var(--border); border-radius: var(--radius); padding: .7rem .8rem; margin-bottom: .5rem; text-decoration: none; color: inherit; transition: border-color .15s, box-shadow .15s; }
    .card:hover, .card:focus-visible { border-color: var(--accent); box-shadow: var(--shadow-card); outline: none; }
    .title { font-weight: 500; margin-bottom: .2rem; }
    .small { font-size: 12px; margin-top: .3rem; }
    .migrate-panel { background: var(--bg-1); border: 1px solid var(--border); border-radius: var(--radius); padding: 16px 20px; margin: 1rem 0; }
    .migrate-panel h3 { margin: 0 0 .25rem; }
    .migrate-result { margin-top: .5rem; display: flex; flex-wrap: wrap; gap: .5rem; align-items: center; }
    @media (max-width: 639px) { .board { gap: .5rem; } .col { flex: 0 0 85vw; } }
  `,
})
export class ProjectPage {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly snack = inject(MatSnackBar);

  readonly id = input.required<string>();
  readonly project = signal<Project | null>(null);
  readonly demands = signal<Demand[]>([]);
  readonly items = signal<WorkItem[]>([]);
  readonly environments = signal<ExecutionEnvironment[]>([]);
  readonly showMigrate = signal(false);
  readonly migrating = signal(false);
  readonly migrateResult = signal<MigrationResult | null>(null);
  migrateTarget: string | null = null;

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

    this.api.environments().subscribe(list => this.environments.set(list.items));

    effect(() => {
      this.id();
      untracked(() => this.loadAll());
    });
  }

  toggleMigrate(): void {
    this.showMigrate.update(v => !v);
    this.migrateResult.set(null);
    this.migrateTarget = null;
  }

  migrate(): void {
    this.migrating.set(true);
    this.migrateResult.set(null);
    this.api.migrateEnvironment(this.id(), this.migrateTarget).subscribe({
      next: (r) => {
        this.migrating.set(false);
        this.migrateResult.set(r);
        this.loadAll();
        this.snack.open('Migração concluída.', 'OK', { duration: 4000 });
      },
      error: (e: HttpErrorResponse) => {
        this.migrating.set(false);
        const msg = (e.error as { error?: string })?.error ?? 'Não foi possível migrar o ambiente.';
        this.snack.open(msg, 'OK', { duration: 6000 });
      },
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
