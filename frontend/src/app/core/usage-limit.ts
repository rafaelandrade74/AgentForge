import { UsageLimitInfo } from './models';

const kindLabels: Record<string, string> = {
  session: 'limite da janela de uso',
  weekly: 'limite semanal',
  rate: 'limite de requisições',
  unknown: 'limite de uso',
};

/** "15:00" se for hoje; "sex 08/11 às 15:00" nos outros dias. Sempre no fuso do navegador. */
export function whenLabel(iso: string, now: Date = new Date()): string {
  const at = new Date(iso);
  const time = at.toLocaleTimeString('pt-BR', { hour: '2-digit', minute: '2-digit' });
  if (at.toDateString() === now.toDateString()) return `às ${time}`;
  const day = at.toLocaleDateString('pt-BR', { weekday: 'short', day: '2-digit', month: '2-digit' });
  return `${day} às ${time}`;
}

/** Frase do aviso global: o que aconteceu e quando o agente volta (ou quando o sistema tenta de novo). */
export function usageLimitText(l: UsageLimitInfo, now: Date = new Date()): string {
  const what = `${l.agent} (${l.environment}): ${kindLabels[l.kind] ?? kindLabels['unknown']} atingido`;
  return l.resetsAt
    ? `${what} — libera ${whenLabel(l.resetsAt, now)}.`
    : `${what} — sem horário de liberação informado; tentando de novo ${whenLabel(l.retryAt, now)}.`;
}
