import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Run, RunSummary } from '../core/models';
import { RunTimelineComponent } from './shared';

const summary: RunSummary = {
  id: 'r1', demandId: null, workItemId: 'w1', role: 'Dev', costUsd: 0.1, durationMs: 1000, status: 'Succeeded',
  startedAt: '2026-01-01T10:00:00Z', finishedAt: '2026-01-01T10:00:01Z',
};
const run: Run = {
  ...summary,
  prompt: '# Tarefa\n- implemente **X**',
  output: 'Feito.\n\n## Mudanças\n- `Api.cs`: novo endpoint\n\n```json\n{"summary":"ok"}\n```\n<script>alert(1)</script>',
};

describe('RunTimelineComponent (saída formatada)', () => {
  let fixture: ComponentFixture<RunTimelineComponent>;
  let http: HttpTestingController;
  const el = () => fixture.nativeElement as HTMLElement;
  const q = (s: string) => el().querySelector(s) as HTMLElement | null;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(RunTimelineComponent);
    fixture.componentRef.setInput('runs', [summary]);
    fixture.detectChanges();
    q('button.head')!.click();
    http.expectOne('/api/agent-runs/r1').flush(run);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('mostra a saída formatada como Markdown, sem executar HTML do texto', () => {
    const out = q('[data-testid="output"]')!;
    expect(out.querySelector('h4')?.textContent).toBe('Mudanças');
    expect(out.querySelector('li code')?.textContent).toBe('Api.cs');
    expect(out.querySelector('pre.md-code')?.textContent).toContain('"summary":"ok"');
    expect(out.querySelector('script')).toBeNull();
    expect(out.textContent).toContain('<script>alert(1)</script>');
    expect(q('pre.block')).toBeNull();
  });

  it('o prompt também sai formatado', () => {
    expect(q('[data-testid="prompt"] strong')?.textContent).toBe('X');
  });

  it('alterna para o texto bruto e volta', () => {
    const toggle = q('button.view-toggle')!;
    expect(toggle.textContent).toContain('Ver texto bruto');

    toggle.click();
    fixture.detectChanges();
    expect(q('pre.block')?.textContent).toContain('## Mudanças');
    expect(q('[data-testid="output"]')).toBeNull();
    expect(q('button.view-toggle')!.textContent).toContain('Ver formatado');

    q('button.view-toggle')!.click();
    fixture.detectChanges();
    expect(q('[data-testid="output"]')).not.toBeNull();
  });
});
