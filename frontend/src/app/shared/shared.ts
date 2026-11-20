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
  template: `<span class="badge" [class]="'badge-' + tone()">{{ label() }}</span>`,
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
    <div #box class="log-box" role="log" aria-live="polite">
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
        <div class="empty">{{ empty() }}</div>
      }
    </div>
  `,
  styles: `
    .log-box { background: var(--bg-2); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 12px; font-family: var(--mono); font-size: 12px; color: var(--text-1); max-height: 200px; overflow-y: auto; line-height: 1.7; }
    .line { padding: 1px 0; white-space: pre-wrap; word-break: break-word; }
    .t { color: var(--text-2); margin-right: .5rem; }
    .who { color: var(--text-1); margin-right: .5rem; }
    .k-run_started .who, .k-run_finished .who { color: var(--accent); font-weight: 600; }
    .k-tool .txt { color: #e0b050; }
    .k-tool_result .txt { color: var(--text-2); }
    .k-result .txt { color: var(--success); }
    .empty { color: var(--text-2); }
    @media (max-width: 639px) { .log-box { max-height: 160px; } }
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
      <div class="accordion-item" [class.open]="expandedId() === r.id">
        <div class="accordion-header" (click)="toggle(r)">
          <div class="agent-avatar" style="width:24px;height:24px;font-size:10px">{{ (roleLabel[r.role] ?? '?')[0] }}</div>
          <span class="when">{{ r.startedAt | date: 'dd/MM HH:mm:ss' }}</span>
          <strong>{{ roleLabel[r.role] }}</strong>
          <app-badge [label]="runLabel[r.status]" [tone]="runTone[r.status]" />
          <span style="flex:1"></span>
          <span class="meta">{{ r.status === 'Running' ? 'em andamento' : duration(r.durationMs) }}</span>
          <span class="meta">{{ usd(r.costUsd) }}</span>
          <span class="accordion-chevron">▾</span>
        </div>
        @if (expandedId() === r.id) {
          <div class="accordion-body">
            @if (detail(); as d) {
              <div class="row-head">
                <h4>Saída</h4>
                <span style="flex:1"></span>
                <button type="button" class="btn btn-secondary btn-sm" (click)="raw.set(!raw())" [attr.aria-pressed]="raw()">
                  {{ raw() ? 'Ver formatado' : 'Ver texto bruto' }}
                </button>
              </div>
              @if (raw()) {
                <div class="log-box" style="max-height:400px;white-space:pre-wrap;word-break:break-word">{{ d.output || '(vazia)' }}</div>
              } @else {
                <div class="formatted"><app-markdown [source]="d.output || '(vazia)'" /></div>
              }
              <details>
                <summary style="cursor:pointer;color:var(--text-2);font-size:13px;margin-top:12px">Prompt enviado</summary>
                @if (raw()) {
                  <div class="log-box" style="max-height:400px;white-space:pre-wrap;word-break:break-word;margin-top:8px">{{ d.prompt }}</div>
                } @else {
                  <div class="formatted" style="margin-top:8px"><app-markdown [source]="d.prompt" /></div>
                }
              </details>
            } @else {
              <span style="color:var(--text-2)">Carregando…</span>
            }
          </div>
        }
      </div>
    } @empty {
      <p style="color:var(--text-2)">Nenhuma execução de agente ainda.</p>
    }
  `,
  styles: `
    .accordion-item { background: var(--bg-1); border: 1px solid var(--border); border-radius: var(--radius); margin-bottom: 8px; overflow: hidden; }
    .accordion-header { padding: 14px 18px; cursor: pointer; display: flex; align-items: center; gap: 12px; font-size: 13px; font-weight: 600; transition: background var(--transition); }
    .accordion-header:hover { background: var(--bg-hover); }
    .accordion-chevron { margin-left: auto; color: var(--text-2); transition: transform .2s; }
    .accordion-item.open .accordion-chevron { transform: rotate(180deg); }
    .accordion-body { padding: 0 18px 18px; }
    .accordion-item:not(.open) .accordion-body { display: none; }
    .agent-avatar { border-radius: 50%; display: grid; place-items: center; font-weight: 700; background: linear-gradient(135deg, #d4a574, #c8956c); color: #fff; flex-shrink: 0; }
    .when { font-size: 12px; color: var(--text-2); }
    .meta { font-size: 12px; color: var(--text-2); }
    h4 { margin: .5rem 0 .25rem; }
    .row-head { display: flex; align-items: center; gap: .5rem; }
    .formatted { background: var(--bg-2); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 12px 16px; max-height: 32rem; overflow: auto; }
    .log-box { background: var(--bg-2); border: 1px solid var(--border); border-radius: var(--radius-sm); padding: 12px; font-family: var(--mono); font-size: 12px; color: var(--text-1); overflow-y: auto; line-height: 1.7; }
  `,
})
export class RunTimelineComponent {
  private readonly api = inject(ApiService);
  readonly runs = input.required<RunSummary[]>();
  readonly expandedId = signal<string | null>(null);
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
