import { Component, ElementRef, effect, inject, input, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MAT_DIALOG_DATA, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { DatePipe } from '@angular/common';
import { ApiService } from '../core/api.service';
import { ROLE_LABEL, RUN_LABEL, RUN_TONE, Tone, formatDuration, formatUsd } from '../core/labels';
import { Run, RunEvent, RunSummary } from '../core/models';
import { MarkdownComponent } from './markdown.component';

@Component({
  selector: 'app-badge',
  standalone: true,
  template: `<span class="badge" [class]="tone()">{{ label() }}</span>`,
})
export class BadgeComponent {
  readonly label = input.required<string>();
  readonly tone = input<Tone>('idle');
}

// ---------------------------------------------------------------- diálogo de confirmação / comentário

export interface ActionDialogData {
  title: string;
  message?: string;
  confirmLabel: string;
  /** Quando definido, mostra um campo de texto e devolve o comentário. */
  commentLabel?: string;
  commentRequired?: boolean;
}

@Component({
  selector: 'app-action-dialog',
  standalone: true,
  imports: [FormsModule, MatDialogModule, MatButtonModule, MatFormFieldModule, MatInputModule],
  template: `
    <h2 mat-dialog-title>{{ data.title }}</h2>
    <mat-dialog-content>
      @if (data.message) {
        <p>{{ data.message }}</p>
      }
      @if (data.commentLabel) {
        <mat-form-field appearance="outline" style="width: 100%">
          <mat-label>{{ data.commentLabel }}</mat-label>
          <textarea matInput rows="4" [(ngModel)]="comment" name="comment"></textarea>
        </mat-form-field>
      }
    </mat-dialog-content>
    <mat-dialog-actions align="end">
      <button mat-button type="button" (click)="ref.close()">Voltar</button>
      <button mat-flat-button type="button" [disabled]="data.commentRequired && !comment.trim()" (click)="ref.close(data.commentLabel ? comment.trim() : true)">
        {{ data.confirmLabel }}
      </button>
    </mat-dialog-actions>
  `,
})
export class ActionDialogComponent {
  readonly data = inject<ActionDialogData>(MAT_DIALOG_DATA);
  readonly ref = inject<MatDialogRef<ActionDialogComponent, string | true>>(MatDialogRef);
  comment = '';
}

// ---------------------------------------------------------------- log ao vivo

export interface LiveLine {
  key: number;
  runId: string;
  kind: string;
  role: string;
  text: string;
  at: string;
}

const MAX_LINES = 600;
let lineSeq = 0;

export function appendLine(lines: LiveLine[], e: RunEvent): LiveLine[] {
  const next = [...lines, { key: ++lineSeq, runId: e.runId, kind: e.kind, role: ROLE_LABEL[e.role], text: e.text, at: e.at }];
  return next.length > MAX_LINES ? next.slice(next.length - MAX_LINES) : next;
}

@Component({
  selector: 'app-live-log',
  standalone: true,
  imports: [DatePipe],
  template: `
    <div #box class="log mono" role="log" aria-live="polite">
      @for (l of lines(); track l.key) {
        <div class="line" [class]="'k-' + l.kind">
          <span class="t">{{ l.at | date: 'HH:mm:ss' }}</span>
          @switch (l.kind) {
            @case ('run_started') { <span class="who">▶ {{ l.role }} iniciou</span> }
            @case ('run_finished') { <span class="who">■ {{ l.role }} terminou: {{ l.text }}</span> }
            @default { <span class="who">{{ l.role }} · {{ l.kind }}</span> <span class="txt">{{ l.text }}</span> }
          }
        </div>
      } @empty {
        <div class="muted">{{ empty() }}</div>
      }
    </div>
  `,
  styles: `
    .log { background: #0d0d0d; border-radius: 6px; padding: .5rem .75rem; height: 18rem; overflow: auto; font-size: .8rem; }
    .line { padding: 1px 0; white-space: pre-wrap; word-break: break-word; }
    .t { color: #777; margin-right: .5rem; }
    .who { color: #9aa; margin-right: .5rem; }
    .k-run_started .who, .k-run_finished .who { color: #b69df8; font-weight: 600; }
    .k-tool .txt { color: #e0b050; }
    .k-tool_result .txt { color: #889; }
    .k-result .txt { color: #7fd48a; }
  `,
})
export class LiveLogComponent {
  readonly lines = input.required<LiveLine[]>();
  readonly empty = input('Nenhuma atividade ao vivo ainda.');
  private readonly box = viewChild<ElementRef<HTMLElement>>('box');

  constructor() {
    effect(() => {
      this.lines();
      const el = this.box()?.nativeElement;
      if (!el) return;
      // rola para o fim só se o usuário já estava perto do fim
      queueMicrotask(() => {
        if (el.scrollHeight - el.scrollTop - el.clientHeight < 80) el.scrollTop = el.scrollHeight;
      });
    });
  }
}

// ---------------------------------------------------------------- linha do tempo das execuções

@Component({
  selector: 'app-run-timeline',
  standalone: true,
  imports: [BadgeComponent, DatePipe, MarkdownComponent],
  template: `
    @for (r of runs(); track r.id) {
      <div class="run">
        <button type="button" class="head" (click)="toggle(r)" [attr.aria-expanded]="expandedId() === r.id">
          <span class="when muted">{{ r.startedAt | date: 'dd/MM HH:mm:ss' }}</span>
          <strong>{{ roleLabel[r.role] }}</strong>
          <app-badge [label]="runLabel[r.status]" [tone]="runTone[r.status]" />
          <span class="spacer"></span>
          <span class="muted">{{ r.status === 'Running' ? 'em andamento' : duration(r.durationMs) }}</span>
          <span class="muted">{{ usd(r.costUsd) }}</span>
        </button>
        @if (expandedId() === r.id) {
          <div class="body">
            @if (detail(); as d) {
              <div class="row-head">
                <h4>Saída</h4>
                <span class="spacer"></span>
                <button type="button" class="view-toggle" (click)="raw.set(!raw())" [attr.aria-pressed]="raw()">
                  {{ raw() ? 'Ver formatado' : 'Ver texto bruto' }}
                </button>
              </div>
              @if (raw()) {
                <pre class="block">{{ d.output || '(vazia)' }}</pre>
              } @else {
                <div class="formatted" data-testid="output"><app-markdown [source]="d.output || '(vazia)'" /></div>
              }
              <details>
                <summary>Prompt enviado</summary>
                @if (raw()) {
                  <pre class="block">{{ d.prompt }}</pre>
                } @else {
                  <div class="formatted" data-testid="prompt"><app-markdown [source]="d.prompt" /></div>
                }
              </details>
            } @else {
              <span class="muted">Carregando…</span>
            }
          </div>
        }
      </div>
    } @empty {
      <p class="muted">Nenhuma execução de agente ainda.</p>
    }
  `,
  styles: `
    .run { border-bottom: 1px solid #2a2a2a; }
    .head { all: unset; cursor: pointer; display: flex; gap: .75rem; align-items: center; padding: .5rem .25rem; width: 100%; box-sizing: border-box; }
    .head:hover { background: #1c1c1c; }
    .head:focus-visible { outline: 2px solid var(--mat-sys-primary, #b69df8); }
    .body { padding: .25rem .5rem 1rem; }
    h4 { margin: .5rem 0 .25rem; }
    .spacer { flex: 1; }
    .row-head { display: flex; align-items: center; gap: .5rem; }
    .view-toggle { background: none; border: 1px solid #444; color: inherit; border-radius: 6px; padding: .15rem .6rem; font: inherit; font-size: .8rem; cursor: pointer; }
    .view-toggle:hover { background: #222; }
    .formatted { background: #131313; border-radius: 6px; padding: .5rem .9rem; max-height: 32rem; overflow: auto; }
  `,
})
export class RunTimelineComponent {
  private readonly api = inject(ApiService);
  readonly runs = input.required<RunSummary[]>();
  readonly expandedId = signal<string | null>(null);
  /** false: saída formatada como Markdown (padrão); true: texto bruto. */
  readonly raw = signal(false);
  readonly detail = signal<Run | null>(null);

  readonly roleLabel = ROLE_LABEL;
  readonly runLabel = RUN_LABEL;
  readonly runTone = RUN_TONE;
  readonly duration = formatDuration;
  readonly usd = formatUsd;

  toggle(run: RunSummary): void {
    if (this.expandedId() === run.id) {
      this.expandedId.set(null);
      return;
    }
    this.expandedId.set(run.id);
    this.detail.set(null);
    this.api.run(run.id).subscribe((d) => {
      if (this.expandedId() === run.id) this.detail.set(d);
    });
  }
}
