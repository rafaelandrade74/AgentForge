import { UsageLimitInfo } from './models';
import { usageLimitText, whenLabel } from './usage-limit';

const base: UsageLimitInfo = {
  environmentId: null, environment: 'Padrão do servidor', agent: 'Claude Code', kind: 'session',
  resetsAt: null, retryAt: '2026-11-07T18:05:00', message: 'hit your limit',
};

describe('usage-limit', () => {
  it('mostra só a hora quando o reset é hoje e o dia quando é outro', () => {
    const now = new Date('2026-11-07T10:00:00');
    expect(whenLabel('2026-11-07T15:00:00', now)).toBe('às 15:00');
    expect(whenLabel('2026-11-09T15:00:00', now)).toContain('09/11');
    expect(whenLabel('2026-11-09T15:00:00', now)).toContain('às 15:00');
  });

  it('diz quando libera, quando o reset é conhecido', () => {
    const text = usageLimitText({ ...base, resetsAt: '2026-11-07T15:00:00' }, new Date('2026-11-07T10:00:00'));
    expect(text).toBe('Claude Code (Padrão do servidor): limite da janela de uso atingido — libera às 15:00.');
  });

  it('avisa que tenta de novo, quando o reset é desconhecido', () => {
    const text = usageLimitText({ ...base, kind: 'weekly' }, new Date('2026-11-07T10:00:00'));
    expect(text).toContain('limite semanal atingido');
    expect(text).toContain('sem horário de liberação informado');
    expect(text).toContain('às 18:05');
  });
});
