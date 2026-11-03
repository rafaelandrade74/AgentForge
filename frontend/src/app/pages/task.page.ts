import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatSnackBar } from '@angular/material/snack-bar';
import { Observable } from 'rxjs';
import { ApiService } from '../core/api.service';
import { DiffLine, diffStats, parseDiff } from '../core/diff';
import {
  ROLE_LABEL, SEVERITY_TONE, STATE_LABEL, STATE_TONE, formatUsd, isTerminal,
} from '../core/labels';
import { Demand, Finding, Project, RunSummary, WorkItem } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import {
  ActionDialogComponent, ActionDialogData, BadgeComponent, LiveLine, LiveLogComponent, RunTimelineComponent, appendLine,
} from '../shared/shared';

const MAX_DIFF_LINES = 4000;

@Component({
  selector: 'app-task-page',
  standalone: true,
  imports: [RouterLink, MatButtonModule, BadgeComponent, LiveLogComponent, RunTimelineComponent],
  template: `
    <div class="page">
      @if (item(); as w) {
        <a [routerLink]="['/demands', w.demandId]">← {{ demand()?.title ?? 'Demanda' }}</a>
        <div class="row">
          <h1>{{ w.title }}</h1>
          <app-badge [label]="stateLabel[w.state]" [tone]="stateTone[w.state]" />
          @if (w.reworkCount > 0) {
            <app-badge [label]="'retrabalho ' + w.reworkCount + '/3'" tone="warn" />
          }
          <span class="spacer"></span>
          <span class="muted">Custo acumulado: <strong>{{ usd(totalCost()) }}</strong></span>
        </div>

        <div class="actions row">
          @if (w.state === 'Backlog') {
            <button mat-flat-button type="button" (click)="approve()">Aprovar tarefa</button>
          }
          @if (w.state === 'AwaitingApproval') {
            <button mat-flat-button type="button" (click)="approve()">Aprovar entrega</button>
            <button mat-stroked-button type="button" (click)="reject()">Rejeitar…</button>
          }
          @if (w.state === 'Blocked') {
            <button mat-flat-button type="button" (click)="retry()">Reexecutar</button>
          }
          @if (!terminal()) {
            <button mat-button type="button" (click)="cancel()">Cancelar tarefa</button>
          }
        </div>

        @if (w.state === 'Blocked' && w.blockedReason) {
          <div class="alert bad" role="alert"><strong>Bloqueada:</strong> {{ w.blockedReason }}</div>
        }
        @if (w.state === 'AwaitingApproval') {
          <div class="alert warn">QA concluído. Revise o diff abaixo e aprove ou rejeite a entrega.</div>
        }

        <div class="cols">
          <section>
            <h2>Descrição</h2>
            <p class="pre">{{ w.description || '—' }}</p>
            <h3>Critérios de aceite</h3>
            <p class="pre">{{ w.acceptanceCriteria || '—' }}</p>
            @if (w.branchName) {
              <p class="muted small">Branch: <span class="mono">{{ w.branchName }}</span> · base {{ project()?.baseBranch }}</p>
            }
          </section>

          <section>
            <h2>Apontamentos de revisão</h2>
            @for (f of findings(); track f.id) {
              <div class="finding" [class.resolved]="f.resolved">
                <div class="row">
                  <app-badge [label]="roleLabel[f.role]" />
                  <app-badge [label]="f.severity" [tone]="severityTone[f.severity]" />
                  @if (f.file) { <span class="mono small">{{ f.file }}{{ f.line ? ':' + f.line : '' }}</span> }
                  <span class="spacer"></span>
                  <span class="muted small">{{ f.resolved ? 'tratado' : 'aberto' }}</span>
                </div>
                <div>{{ f.message }}</div>
              </div>
            } @empty {
              <p class="muted">Nenhum apontamento.</p>
            }
          </section>
        </div>

        <h2>Ao vivo</h2>
        <app-live-log [lines]="live()" empty="Nenhuma execução em andamento." />

        <h2>Execuções <span class="muted small">· {{ runs().length }}</span></h2>
        <app-run-timeline [runs]="runs()" />

        <div class="row">
          <h2>Diff do branch</h2>
          @if (stats(); as s) {
            <span class="add">+{{ s.added }}</span> <span class="del">−{{ s.removed }}</span>
          }
          <span class="spacer"></span>
          <button mat-button type="button" (click)="loadDiff()">Atualizar</button>
        </div>
        @if (diffLines().length > 0) {
          <div class="diff mono" role="region" aria-label="Diff do branch">
            @for (l of shownDiff(); track $index) {
              <div [class]="'dl ' + l.kind">{{ l.text }}</div>
            }
            @if (diffLines().length > shownDiff().length) {
              <div class="muted">… {{ diffLines().length - shownDiff().length }} linhas omitidas.</div>
            }
          </div>
        } @else {
          <p class="muted">{{ diffLoaded() ? 'O branch ainda não tem mudanças em relação à base.' : 'Carregando…' }}</p>
        }
      } @else {
        <p class="muted">Carregando…</p>
      }
    </div>
  `,
  styles: `
    .actions { margin: .75rem 0; }
    .alert { padding: .6rem .9rem; border-radius: 8px; margin: .5rem 0; }
    .alert.bad { background: #3a1818; color: #ffb4b4; }
    .alert.warn { background: #3a3014; color: #ffe08a; }
    .cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(22rem, 1fr)); gap: 1.5rem; }
    .pre { white-space: pre-wrap; }
    .small { font-size: .8rem; }
    .finding { background: #1b1b1b; border-radius: 8px; padding: .5rem .7rem; margin-bottom: .5rem; }
    .finding.resolved { opacity: .55; }
    .diff { background: #0d0d0d; border-radius: 6px; padding: .5rem 0; overflow: auto; max-height: 36rem; font-size: .8rem; }
    .dl { padding: 0 .75rem; white-space: pre; }
    .dl.add { background: #12301b; color: #9be5ad; }
    .dl.del { background: #3a1616; color: #f0a5a5; }
    .dl.hunk { color: #8ab4f8; background: #151d2b; }
    .dl.meta { color: #aaa; font-weight: 600; }
    .add { color: #7fd48a; } .del { color: #f08a8a; }
  `,
})
export class TaskPage {
  private readonly api = inject(ApiService);
  private readonly realtime = inject(RealtimeService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly id = input.required<string>();
  readonly item = signal<WorkItem | null>(null);
  readonly demand = signal<Demand | null>(null);
  readonly project = signal<Project | null>(null);
  readonly runs = signal<RunSummary[]>([]);
  readonly findings = signal<Finding[]>([]);
  readonly live = signal<LiveLine[]>([]);
  readonly diffLines = signal<DiffLine[]>([]);
  readonly diffLoaded = signal(false);

  readonly stateLabel = STATE_LABEL;
  readonly stateTone = STATE_TONE;
  readonly roleLabel = ROLE_LABEL;
  readonly severityTone = SEVERITY_TONE;
  readonly usd = formatUsd;
  readonly totalCost = computed(() => this.runs().reduce((sum, r) => sum + r.costUsd, 0));
  readonly terminal = computed(() => { const s = this.item()?.state; return !s || isTerminal(s); });
  readonly stats = computed(() => (this.diffLines().length ? diffStats(this.diffLines()) : null));
  readonly shownDiff = computed(() => this.diffLines().slice(0, MAX_DIFF_LINES));

  constructor() {
    this.realtime.bindProject(() => this.project()?.id ?? null);

    this.realtime.runEvent$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.workItemId !== this.id()) return;
      this.live.update((l) => appendLine(l, e));
      if (e.kind === 'run_started' || e.kind === 'run_finished') this.loadRuns();
    });
    this.realtime.workItemChanged$.pipe(takeUntilDestroyed()).subscribe((e) => {
      if (e.workItemId !== this.id()) return;
      const previous = this.item()?.state;
      this.item.update((w) => (w
        ? { ...w, state: e.state, reworkCount: e.reworkCount, blockedReason: e.blockedReason, branchName: e.branchName }
        : w));
      this.loadFindings();
      this.loadRuns();
      if (previous !== e.state) this.loadDiff();
    });
    this.realtime.reconnected$.pipe(takeUntilDestroyed()).subscribe(() => this.loadAll());

    effect(() => {
      this.id();
      untracked(() => this.loadAll());
    });
  }

  // ---- ações

  approve(): void {
    this.act(this.api.approve(this.id()), 'Aprovada.');
  }

  reject(): void {
    this.confirm({
      title: 'Rejeitar entrega',
      message: 'A tarefa volta ao Dev com o seu comentário como apontamento.',
      commentLabel: 'O que precisa mudar?',
      commentRequired: true,
      confirmLabel: 'Rejeitar',
    }).subscribe((comment) => {
      if (typeof comment === 'string') this.act(this.api.reject(this.id(), comment), 'Devolvida ao Dev.');
    });
  }

  retry(): void {
    this.act(this.api.retry(this.id()), 'Tarefa devolvida ao Dev.');
  }

  cancel(): void {
    this.confirm({
      title: 'Cancelar tarefa',
      message: 'O agente em execução será interrompido e o worktree e o branch da tarefa serão apagados.',
      confirmLabel: 'Cancelar tarefa',
    }).subscribe((ok) => {
      if (ok) this.act(this.api.cancel(this.id()), 'Tarefa cancelada.');
    });
  }

  private confirm(data: ActionDialogData): Observable<string | true | undefined> {
    return this.dialog.open<ActionDialogComponent, ActionDialogData, string | true>(ActionDialogComponent, { data, width: '32rem' }).afterClosed();
  }

  private act(request: Observable<void>, okMessage: string): void {
    request.subscribe({
      next: () => {
        this.snack.open(okMessage, 'OK', { duration: 3000 });
        this.loadItem();
      },
      error: (err) => this.snack.open(err?.status === 409 ? 'Essa ação não vale mais para o estado atual da tarefa.' : 'Não foi possível executar a ação.', 'OK', { duration: 5000 }),
    });
  }

  // ---- carregamento

  loadDiff(): void {
    this.api.diff(this.id()).subscribe((text) => {
      this.diffLines.set(parseDiff(text));
      this.diffLoaded.set(true);
    });
  }

  private loadAll(): void {
    this.loadItem(true);
    this.loadRuns();
    this.loadFindings();
    this.loadDiff();
  }

  private loadItem(withContext = false): void {
    this.api.workItem(this.id()).subscribe((w) => {
      this.item.set(w);
      if (!withContext) return;
      this.api.demand(w.demandId).subscribe((d) => {
        this.demand.set(d);
        this.api.project(d.projectId).subscribe((p) => this.project.set(p));
      });
    });
  }

  private loadRuns(): void {
    this.api.runsOfWorkItem(this.id()).subscribe((r) => this.runs.set(r));
  }

  private loadFindings(): void {
    this.api.findings(this.id()).subscribe((f) => this.findings.set(f));
  }
}
