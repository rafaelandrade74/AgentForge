import { Injectable, computed, inject, signal } from '@angular/core';
import { ApiService } from './api.service';
import { AgentInfo, UsageLimitInfo } from './models';

/** Estado compartilhado dos agentes: a tela Agentes e o aviso global da barra leem daqui. */
@Injectable({ providedIn: 'root' })
export class AgentsService {
  private readonly api = inject(ApiService);

  readonly agents = signal<AgentInfo[] | null>(null);

  /** Agentes que algum papel usa mas que não estão prontos (CLI ausente ou não conectado). */
  readonly needsAttention = computed(() =>
    (this.agents() ?? []).filter((a) => a.usedByRoles.length > 0 && (!a.installed || !a.authenticated)));

  /** Limites de uso em vigor (créditos/janela de horas esgotados): as tarefas esperam em vez de falhar. */
  readonly usageLimits = signal<UsageLimitInfo[]>([]);

  refreshLimits(): void {
    this.api.usageLimits().subscribe({ next: (l) => this.usageLimits.set(l), error: () => undefined });
  }

  refresh(): void {
    this.api.agents().subscribe({ next: (a) => this.agents.set(a), error: () => undefined });
  }

  replace(info: AgentInfo): void {
    this.agents.update((list) => (list ?? []).map((a) => (a.name === info.name ? { ...info, usedByRoles: a.usedByRoles } : a)));
  }
}
