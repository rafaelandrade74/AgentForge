import { AgentRole, DemandStatus, RunStatus, Severity, WorkItemState } from './models';

export type Tone = 'ok' | 'warn' | 'bad' | 'info' | 'idle';

export const STATE_LABEL: Record<WorkItemState, string> = {
  Backlog: 'Backlog',
  Todo: 'A fazer',
  InDevelopment: 'Desenvolvimento',
  TechLeadReview: 'Revisão Tech Lead',
  SecurityReview: 'Revisão Security',
  Qa: 'QA',
  AwaitingApproval: 'Aguardando aprovação',
  Done: 'Done',
  Blocked: 'Bloqueada',
  Cancelled: 'Cancelada',
};

export const STATE_TONE: Record<WorkItemState, Tone> = {
  Backlog: 'idle',
  Todo: 'idle',
  InDevelopment: 'info',
  TechLeadReview: 'info',
  SecurityReview: 'info',
  Qa: 'info',
  AwaitingApproval: 'warn',
  Done: 'ok',
  Blocked: 'bad',
  Cancelled: 'idle',
};

export const DEMAND_LABEL: Record<DemandStatus, string> = {
  New: 'Nova',
  Refining: 'Em refinamento',
  InProgress: 'Em andamento',
  Done: 'Concluída',
  Cancelled: 'Cancelada',
  Failed: 'Falhou',
};

export const DEMAND_TONE: Record<DemandStatus, Tone> = {
  New: 'idle',
  Refining: 'info',
  InProgress: 'info',
  Done: 'ok',
  Cancelled: 'idle',
  Failed: 'bad',
};

export const ROLE_LABEL: Record<AgentRole, string> = {
  ScrumMaster: 'Scrum Master',
  Dev: 'Dev',
  TechLead: 'Tech Lead',
  Security: 'Security',
  Qa: 'QA',
  Human: 'Revisão humana',
};

export const RUN_LABEL: Record<RunStatus, string> = {
  Pending: 'Na fila',
  Running: 'Executando',
  Succeeded: 'Concluída',
  Failed: 'Falhou',
  Cancelled: 'Cancelada',
};

export const RUN_TONE: Record<RunStatus, Tone> = {
  Pending: 'idle',
  Running: 'info',
  Succeeded: 'ok',
  Failed: 'bad',
  Cancelled: 'idle',
};

export const SEVERITY_TONE: Record<Severity, Tone> = {
  Info: 'idle',
  Low: 'info',
  Medium: 'warn',
  High: 'bad',
  Critical: 'bad',
};

export const isTerminal = (s: WorkItemState) => s === 'Done' || s === 'Cancelled';

/** Colunas do quadro, na ordem do fluxo. Canceladas ficam fora do quadro. */
export const BOARD_STATES: WorkItemState[] = [
  'Backlog', 'Todo', 'InDevelopment', 'TechLeadReview', 'SecurityReview', 'Qa', 'AwaitingApproval', 'Blocked', 'Done',
];

export function groupByState<T extends { state: WorkItemState }>(items: T[]): Record<WorkItemState, T[]> {
  const groups = Object.fromEntries(
    (Object.keys(STATE_LABEL) as WorkItemState[]).map((s) => [s, [] as T[]]),
  ) as Record<WorkItemState, T[]>;
  for (const item of items) groups[item.state].push(item);
  return groups;
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms} ms`;
  const s = Math.round(ms / 1000);
  if (s < 60) return `${s} s`;
  return `${Math.floor(s / 60)} min ${s % 60} s`;
}

export const formatUsd = (n: number) => `US$ ${n.toFixed(4)}`;
