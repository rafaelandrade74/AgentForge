import { LiveAgent } from '../core/models';
import { STATE_LABEL, agentState, elapsed } from './live.page';

const base: LiveAgent = {
  environmentId: null, environment: 'Padrão do servidor', mode: 'local', agent: 'claude-code', displayName: 'Claude Code',
  reachable: true, installed: true, authenticated: true, version: '2.1.316', detail: null, runningNow: 0, usageLimit: null,
};

describe('agentState', () => {
  it('mostra o problema mais grave primeiro', () => {
    expect(agentState({ ...base, reachable: false, installed: false })).toBe('unreachable');
    expect(agentState({ ...base, installed: false })).toBe('not-installed');
    expect(agentState({ ...base, authenticated: false })).toBe('not-connected');
    expect(agentState({ ...base, runningNow: 2, usageLimit: { environmentId: null, environment: 'x', agent: 'Claude Code', kind: 'session', resetsAt: null, retryAt: '2026-11-09T10:00:00Z', message: 'm' } }))
      .toBe('limited');
  });

  it('distingue ocupado de pronto', () => {
    expect(agentState(base)).toBe('ready');
    expect(agentState({ ...base, runningNow: 1 })).toBe('busy');
    expect(STATE_LABEL['ready']).toBe('pronto');
  });
});

describe('elapsed', () => {
  const start = '2026-11-09T10:00:00Z';

  it('conta segundos e minutos desde o início', () => {
    expect(elapsed(start, new Date('2026-11-09T10:00:07Z').getTime())).toBe('7 s');
    expect(elapsed(start, new Date('2026-11-09T10:02:05Z').getTime())).toBe('2 min 5 s');
  });

  it('nunca fica negativo (relógios diferentes)', () => {
    expect(elapsed(start, new Date('2026-11-09T09:59:50Z').getTime())).toBe('0 s');
  });
});
