import { Injectable, effect, inject, signal, untracked } from '@angular/core';
import { HttpTransportType, HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { AuthService } from '../auth/auth.service';
import { DemandChanged, RunEvent, WorkItemChanged } from './models';

export type RealtimeStatus = 'disconnected' | 'connecting' | 'connected' | 'reconnecting';

/**
 * Conexão única com /hubs/runs. As telas pedem para entrar no grupo de um projeto (bindProject) e assinam os
 * Subjects. Os grupos do SignalR se perdem numa reconexão, então o serviço entra de novo e avisa por reconnected$
 * para a tela recarregar o estado por REST (eventos perdidos não são reenviados).
 */
@Injectable({ providedIn: 'root' })
export class RealtimeService {
  private readonly auth = inject(AuthService);

  readonly status = signal<RealtimeStatus>('disconnected');
  readonly runEvent$ = new Subject<RunEvent>();
  readonly workItemChanged$ = new Subject<WorkItemChanged>();
  readonly demandChanged$ = new Subject<DemandChanged>();
  readonly reconnected$ = new Subject<void>();

  private connection: HubConnection | null = null;
  private connecting: Promise<HubConnection | null> | null = null;
  private retryTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly joined = new Map<string, number>();

  /** Chamar em contexto de injeção (construtor do componente): entra/sai do grupo conforme o id muda. */
  bindProject(projectId: () => string | null): void {
    effect((onCleanup) => {
      const id = projectId();
      if (!id) return;
      let left = false;
      // join/leave escrevem em signals (status); fora do contexto reativo do effect
      untracked(() => void this.join(id));
      onCleanup(() => {
        if (!left) {
          left = true;
          untracked(() => void this.leave(id));
        }
      });
    });
  }

  async join(projectId: string): Promise<void> {
    this.joined.set(projectId, (this.joined.get(projectId) ?? 0) + 1);
    const connection = await this.ensureConnected();
    if (connection && (this.joined.get(projectId) ?? 0) > 0) await this.invoke(connection, 'JoinProject', projectId);
  }

  async leave(projectId: string): Promise<void> {
    const count = (this.joined.get(projectId) ?? 0) - 1;
    if (count > 0) {
      this.joined.set(projectId, count);
      return;
    }
    this.joined.delete(projectId);
    if (this.connection?.state === HubConnectionState.Connected) await this.invoke(this.connection, 'LeaveProject', projectId);
    if (this.joined.size === 0) await this.disconnect();
  }

  private ensureConnected(): Promise<HubConnection | null> {
    if (this.connection?.state === HubConnectionState.Connected) return Promise.resolve(this.connection);
    this.connecting ??= this.connect().finally(() => (this.connecting = null));
    return this.connecting;
  }

  private async connect(): Promise<HubConnection | null> {
    const connection = this.connection ?? this.build();
    this.connection = connection;
    this.status.set('connecting');
    try {
      await connection.start();
      this.status.set('connected');
      await this.rejoinAll(connection);
      return connection;
    } catch {
      this.status.set('disconnected');
      this.scheduleRetry();
      return null;
    }
  }

  private build(): HubConnection {
    const connection = new HubConnectionBuilder()
      .withUrl('/hubs/runs', {
        accessTokenFactory: () => this.auth.freshAccessToken(),
        transport: HttpTransportType.WebSockets | HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect([0, 1000, 3000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    connection.on('runEvent', (e: RunEvent) => this.runEvent$.next(e));
    connection.on('workItemChanged', (e: WorkItemChanged) => this.workItemChanged$.next(e));
    connection.on('demandChanged', (e: DemandChanged) => this.demandChanged$.next(e));
    connection.onreconnecting(() => this.status.set('reconnecting'));
    connection.onreconnected(async () => {
      this.status.set('connected');
      await this.rejoinAll(connection);
      this.reconnected$.next();
    });
    connection.onclose(() => {
      this.status.set('disconnected');
      if (this.joined.size > 0) this.scheduleRetry();
    });
    return connection;
  }

  private async rejoinAll(connection: HubConnection): Promise<void> {
    for (const id of this.joined.keys()) await this.invoke(connection, 'JoinProject', id);
  }

  private async invoke(connection: HubConnection, method: string, ...args: unknown[]): Promise<void> {
    try {
      await connection.invoke(method, ...args);
    } catch {
      // a reconexão automática ou o retry cuidam; a tela continua funcionando por REST
    }
  }

  /** Se a primeira conexão falhar (API reiniciando, token vencido), tenta de novo enquanto houver tela interessada. */
  private scheduleRetry(): void {
    if (this.retryTimer || this.joined.size === 0) return;
    this.retryTimer = setTimeout(async () => {
      this.retryTimer = null;
      if (this.joined.size === 0) return;
      const connection = await this.ensureConnected();
      if (connection) this.reconnected$.next();
    }, 5000);
  }

  private async disconnect(): Promise<void> {
    if (this.retryTimer) clearTimeout(this.retryTimer);
    this.retryTimer = null;
    const connection = this.connection;
    this.connection = null;
    this.status.set('disconnected');
    if (connection) await connection.stop();
  }
}
