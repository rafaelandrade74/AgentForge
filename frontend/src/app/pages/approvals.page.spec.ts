import { waitingFor } from './approvals.page';

describe('waitingFor', () => {
  const now = new Date('2026-11-07T12:00:00Z');

  it('descreve há quanto tempo a entrega espera', () => {
    expect(waitingFor('2026-11-07T11:59:40Z', now)).toBe('agora há pouco');
    expect(waitingFor('2026-11-07T11:15:00Z', now)).toBe('há 45 min');
    expect(waitingFor('2026-11-07T07:00:00Z', now)).toBe('há 5 h');
    expect(waitingFor('2026-11-03T12:00:00Z', now)).toBe('há 4 dias');
  });

  it('fica vazio sem data', () => {
    expect(waitingFor(null, now)).toBe('');
  });
});
