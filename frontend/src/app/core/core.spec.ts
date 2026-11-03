import { BOARD_STATES, formatDuration, formatUsd, groupByState, isTerminal } from './labels';
import { diffStats, parseDiff } from './diff';
import { WorkItemState } from './models';
import { appendLine } from '../shared/shared';

describe('labels', () => {
  it('agrupa as tarefas por estado, com todas as colunas presentes', () => {
    const items = [
      { id: 'a', state: 'Todo' as WorkItemState },
      { id: 'b', state: 'Todo' as WorkItemState },
      { id: 'c', state: 'Done' as WorkItemState },
    ];
    const groups = groupByState(items);
    expect(groups.Todo.map((i) => i.id)).toEqual(['a', 'b']);
    expect(groups.Done.length).toBe(1);
    expect(groups.Blocked).toEqual([]);
    for (const s of BOARD_STATES) expect(groups[s]).toBeDefined();
  });

  it('o quadro segue a ordem do fluxo e deixa canceladas de fora', () => {
    expect(BOARD_STATES[0]).toBe('Backlog');
    expect(BOARD_STATES.indexOf('Qa')).toBeLessThan(BOARD_STATES.indexOf('AwaitingApproval'));
    expect(BOARD_STATES).not.toContain('Cancelled');
  });

  it('só Done e Cancelled são terminais', () => {
    expect(isTerminal('Done')).toBeTrue();
    expect(isTerminal('Cancelled')).toBeTrue();
    expect(isTerminal('Blocked')).toBeFalse();
  });

  it('formata duração e custo', () => {
    expect(formatDuration(250)).toBe('250 ms');
    expect(formatDuration(12_400)).toBe('12 s');
    expect(formatDuration(125_000)).toBe('2 min 5 s');
    expect(formatUsd(0.17312)).toBe('US$ 0.1731');
  });
});

describe('parseDiff', () => {
  const diff = [
    'diff --git a/a.txt b/a.txt',
    'index 111..222 100644',
    '--- a/a.txt',
    '+++ b/a.txt',
    '@@ -1,2 +1,2 @@',
    ' contexto',
    '-removida',
    '+adicionada',
    '',
  ].join('\n');

  it('classifica as linhas', () => {
    const kinds = parseDiff(diff).map((l) => l.kind);
    expect(kinds).toEqual(['meta', 'meta', 'meta', 'meta', 'hunk', 'ctx', 'del', 'add']);
  });

  it('conta adições e remoções sem contar o cabeçalho', () => {
    expect(diffStats(parseDiff(diff))).toEqual({ added: 1, removed: 1 });
  });

  it('diff vazio não gera linhas', () => {
    expect(parseDiff('')).toEqual([]);
  });
});

describe('appendLine', () => {
  const event = (n: number) => ({
    runId: 'r', projectId: 'p', demandId: 'd', workItemId: 'w', role: 'Dev' as const, kind: 'log', text: `l${n}`, at: '2026-01-01T00:00:00Z',
  });

  it('acrescenta no fim e limita o tamanho do log', () => {
    let lines = appendLine([], event(0));
    for (let i = 1; i < 650; i++) lines = appendLine(lines, event(i));
    expect(lines.length).toBe(600);
    expect(lines[lines.length - 1].text).toBe('l649');
    expect(lines[0].text).toBe('l50');
    expect(lines[0].role).toBe('Dev');
  });
});
