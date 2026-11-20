import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { AgentsService } from '../core/agents.service';
import { ROLE_LABEL, formatDuration, formatUsd } from '../core/labels';
import { AgentInfo, AgentRole, AgentTestResult, LoginSession, RoleConfig } from '../core/models';
import { EnvironmentSelectComponent } from '../shared/environment-select.component';
import { BadgeComponent } from '../shared/shared';

interface LoginUi {
  session: LoginSession | null;
  code: string;
  busy: boolean;
  error: string | null;
}

@Component({
  selector: 'app-agents-page',
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, BadgeComponent, EnvironmentSelectComponent],
  template: `
    <div class="content-area">
      <div class="page-header">
        <div>
          <div class="page-title">Agentes</div>
          <div class="page-subtitle">Configuração e status dos agentes CLI</div>
        </div>
      </div>

      <div style="margin-bottom:16px">
        <app-environment-select [value]="environmentId()" (changed)="select($event)" />
      </div>

      @if (environment(); as env) {
        <div style="display:flex;gap:6px;align-items:center;margin-bottom:16px;font-size:12px">
          <app-badge [label]="env.mode === 'host' ? 'CLI no seu computador' : 'CLI neste servidor'" [tone]="env.mode === 'host' ? 'info' : 'idle'" />
          <span style="color:var(--text-2)">{{ env.description }}</span>
        </div>
      }

      <div class="grid-auto" style="grid-template-columns:repeat(auto-fill,minmax(300px,1fr))">
        @for (a of agents(); track a.name) {
          <div class="card card-p" [attr.aria-label]="a.displayName">
            <div style="display:flex;align-items:center;gap:10px;margin-bottom:10px">
              <div class="agent-avatar">{{ a.displayName?.charAt(0) ?? 'A' }}</div>
              <div style="flex:1">
                <div style="font-size:14px;font-weight:600">{{ a.displayName }}</div>
                @if (a.version) { <div style="font-size:11px;color:var(--text-2)">v{{ a.version }}</div> }
              </div>
              @if (!a.installed) {
                <span class="badge badge-bad">Não instalado</span>
              } @else if (a.authenticated) {
                <span class="badge badge-ok">Conectado</span>
              } @else {
                <span class="badge badge-warn">Não conectado</span>
              }
            </div>

            <div style="display:flex;gap:4px;flex-wrap:wrap;margin-bottom:10px">
              @for (r of a.usedByRoles; track r) { <span class="badge badge-accent">{{ roleLabel[r] }}</span> }
            </div>

            @if (a.authenticated && a.detail) {
              <div style="font-size:12px;color:var(--text-2);margin-bottom:8px">Conta: {{ a.detail }}@if (a.authMethod) { · {{ a.authMethod }} }</div>
            }
            @if (!a.installed) {
              <div style="font-size:12px;color:var(--danger);margin-bottom:8px">
                {{ a.detail }}
                {{ environmentId() ? 'Confira o host agent e o CLI.' : 'Reconstrua a imagem da API.' }}
              </div>
            }

            @if (a.installed && a.authenticated) {
              <div style="margin-bottom:8px">
                @if (testing()[a.name]) {
                  <span style="font-size:12px;color:var(--text-2)">Testando…</span>
                } @else if (a.lastTest) {
                  <div style="display:flex;align-items:center;gap:6px;font-size:12px">
                    <span class="badge" [class]="a.lastTest.ok ? 'badge-ok' : 'badge-bad'">{{ a.lastTest.ok ? 'Teste OK' : 'Falhou' }}</span>
                    <span style="color:var(--text-2)">"{{ a.lastTest.message }}" · {{ duration(a.lastTest.durationMs) }} · {{ usd(a.lastTest.costUsd) }}</span>
                  </div>
                }
              </div>
              <div style="display:flex;gap:6px">
                <button class="btn btn-secondary btn-sm" type="button" [disabled]="testing()[a.name]" (click)="test(a)">Testar</button>
                @if (a.supportsLogin) {
                  <button class="btn btn-secondary btn-sm" type="button" (click)="logout(a)">Desconectar</button>
                }
              </div>
            }

            @if (a.installed && !a.authenticated) {
              @if (a.supportsLogin) {
                @if (loginOf(a.name); as ui) {
                  <div style="border-top:1px solid var(--border);margin-top:10px;padding-top:10px">
                    @if (!ui.session && !ui.error) {
                      <span style="font-size:12px;color:var(--text-2)">Gerando o link…</span>
                    } @else if (ui.session?.state === 'WaitingForCode') {
                      <div style="font-size:12px;margin-bottom:8px">
                        1. <a [href]="ui.session?.url" target="_blank" rel="noopener noreferrer" style="color:var(--accent)">Abrir página de login</a><br>
                        2. Cole o código:
                      </div>
                      <div style="display:flex;gap:6px">
                        <input class="form-input" style="flex:1" placeholder="Código" [value]="ui.code" (input)="setCode(a.name, $any($event.target).value)" (keydown.enter)="sendCode(a)" autocomplete="off">
                        <button class="btn btn-primary btn-sm" type="button" [disabled]="!ui.code.trim() || ui.busy" (click)="sendCode(a)">OK</button>
                        <button class="btn btn-secondary btn-sm" type="button" (click)="cancelLogin(a)">×</button>
                      </div>
                    } @else {
                      <button class="btn btn-primary btn-sm" type="button" (click)="startLogin(a)">Tentar de novo</button>
                    }
                    @if (ui.busy) { <div style="font-size:12px;color:var(--text-2);margin-top:4px">Verificando…</div> }
                    @if (ui.error) { <div style="font-size:12px;color:var(--danger);margin-top:4px">{{ ui.error }}</div> }
                  </div>
                } @else {
                  <div style="display:flex;gap:6px;margin-top:8px">
                    <button class="btn btn-primary btn-sm" type="button" (click)="startLogin(a)">Conectar</button>
                  </div>
                }
              } @else {
                <div style="font-size:12px;color:var(--text-2);margin-top:8px">Conecte direto no servidor.</div>
              }
            }
          </div>
        } @empty {
          <p style="color:var(--text-2)">Carregando agentes…</p>
        }
      </div>

      <div class="section-title" style="margin-top:24px">Agente de cada papel</div>
      <div class="grid-auto" style="grid-template-columns:repeat(auto-fill,minmax(260px,1fr))">
        @for (c of configs(); track c.id) {
          <div class="card card-p" style="display:flex;align-items:center;gap:10px">
            <span style="font-size:13px;font-weight:500;min-width:90px">{{ roleLabel[c.role] }}</span>
            <mat-form-field appearance="outline" subscriptSizing="dynamic" style="flex:1">
              <mat-label>Agente</mat-label>
              <mat-select [value]="c.adapterName" (selectionChange)="assign(c, $event.value)" [attr.aria-label]="'Agente do papel ' + roleLabel[c.role]">
                @for (a of agents(); track a.name) {
                  <mat-option [value]="a.name">{{ a.displayName }}{{ a.authenticated ? '' : ' (não conectado)' }}</mat-option>
                }
              </mat-select>
            </mat-form-field>
          </div>
        }
      </div>
    </div>
  `,
  styles: `
    .content-area { padding: 24px; max-width: 1200px; }
    @media (max-width: 639px) { .content-area { padding: 16px; } }
  `,
})
export class AgentsPage {
  private readonly api = inject(ApiService);
  private readonly shared = inject(AgentsService);
  private readonly snack = inject(MatSnackBar);

  /** Agentes do ambiente escolhido. Só o ambiente padrão alimenta o estado global (o aviso da barra lateral). */
  private readonly list = signal<AgentInfo[] | null>(null);
  readonly agents = () => this.list() ?? [];
  /** Máquina em que a tela trabalha; nulo = padrão do servidor. Pode vir de ?environmentId= (link da tela Ambientes). */
  readonly environmentId = signal<string | null>(
    inject(ActivatedRoute, { optional: true })?.snapshot.queryParamMap.get('environmentId') ?? null);
  readonly configs = signal<RoleConfig[]>([]);
  readonly testing = signal<Record<string, boolean>>({});
  readonly environment = signal<{ mode: 'local' | 'host'; description: string } | null>(null);
  private readonly logins = signal<Record<string, LoginUi>>({});

  readonly roleLabel = ROLE_LABEL;
  readonly duration = formatDuration;
  readonly usd = formatUsd;

  constructor() {
    this.api.roleConfigs().subscribe((c) => {
      const order = Object.keys(ROLE_LABEL);
      this.configs.set(c.filter((x) => x.role !== 'Human').sort((a, b) => order.indexOf(a.role) - order.indexOf(b.role)));
    });
    this.load();
  }

  select(environmentId: string | null): void {
    this.environmentId.set(environmentId);
    this.logins.set({});
    this.testing.set({});
    this.load();
  }

  private load(): void {
    const env = this.environmentId();
    this.list.set(null);
    this.environment.set(null);
    this.api.agentEnvironment(env).subscribe({ next: (e) => this.environment.set(e), error: () => undefined });
    this.api.agents(env).subscribe({
      next: (list) => {
        this.setList(list);
        // já conectado e ainda sem teste: testa agora (o servidor guarda o resultado, então não repete a cada visita)
        for (const a of list) if (a.installed && a.authenticated && !a.lastTest) this.test(a);
      },
      error: (err) => this.snack.open(err?.error?.error ?? 'Não foi possível carregar os agentes.', 'OK', { duration: 5000 }),
    });
  }

  loginOf(name: string): LoginUi | undefined {
    return this.logins()[name];
  }

  // ---- teste

  test(a: AgentInfo): void {
    this.testing.update((t) => ({ ...t, [a.name]: true }));
    this.api.testAgent(a.name, this.environmentId()).subscribe({
      next: (result) => this.finishTest(a.name, result),
      error: () => this.finishTest(a.name, { ok: false, message: 'Não foi possível executar o teste.', durationMs: 0, costUsd: 0, at: new Date().toISOString() }),
    });
  }

  private finishTest(name: string, result: AgentTestResult): void {
    this.testing.update((t) => ({ ...t, [name]: false }));
    this.setList((this.list() ?? []).map((x) => (x.name === name ? { ...x, lastTest: result } : x)));
  }

  // ---- login por link

  startLogin(a: AgentInfo): void {
    this.setLogin(a.name, { session: null, code: '', busy: false, error: null });
    this.api.startLogin(a.name, this.environmentId()).subscribe({
      next: (session) => {
        const waiting = session.state === 'WaitingForCode';
        this.setLogin(a.name, { session, code: '', busy: false, error: waiting ? null : session.message ?? 'Não foi possível iniciar o login.' });
      },
      error: () => this.setLogin(a.name, { session: null, code: '', busy: false, error: 'Não foi possível iniciar o login.' }),
    });
  }

  setCode(name: string, code: string): void {
    const ui = this.logins()[name];
    if (ui) this.setLogin(name, { ...ui, code });
  }

  sendCode(a: AgentInfo): void {
    const ui = this.logins()[a.name];
    if (!ui?.session || !ui.code.trim() || ui.busy) return;
    this.setLogin(a.name, { ...ui, busy: true, error: null });
    this.api.sendLoginCode(a.name, ui.session.sessionId, ui.code.trim(), this.environmentId()).subscribe({
      next: (session) => {
        if (session.state === 'Succeeded') {
          this.clearLogin(a.name);
          this.snack.open(session.message ?? 'Conectado.', 'OK', { duration: 4000 });
          this.reloadAndTest(a.name);
        } else if (session.state === 'WaitingForCode') {
          this.setLogin(a.name, { session, code: ui.code, busy: false, error: session.message ?? 'Código não aceito.' });
        } else {
          this.setLogin(a.name, { session, code: '', busy: false, error: session.message ?? 'O login não foi concluído.' });
        }
      },
      error: () => this.setLogin(a.name, { ...ui, busy: false, error: 'Não foi possível enviar o código.' }),
    });
  }

  cancelLogin(a: AgentInfo): void {
    const ui = this.logins()[a.name];
    if (ui?.session) this.api.cancelLogin(a.name, ui.session.sessionId, this.environmentId()).subscribe({ error: () => undefined });
    this.clearLogin(a.name);
  }

  private reloadAndTest(name: string): void {
    this.api.agent(name, true, this.environmentId()).subscribe((info) => {
      this.replace(info);
      if (info.installed && info.authenticated) this.test(info);
    });
  }

  logout(a: AgentInfo): void {
    this.api.logoutAgent(a.name, this.environmentId()).subscribe({
      next: (info) => this.replace(info),
      error: () => this.snack.open('Não foi possível desconectar.', 'OK', { duration: 5000 }),
    });
  }

  private setList(list: AgentInfo[]): void {
    this.list.set(list);
    if (this.environmentId() === null) this.shared.agents.set(list);
  }

  private replace(info: AgentInfo): void {
    this.setList((this.list() ?? []).map((a) => (a.name === info.name ? { ...info, usedByRoles: a.usedByRoles } : a)));
  }

  private setLogin(name: string, ui: LoginUi): void {
    this.logins.update((l) => ({ ...l, [name]: ui }));
  }

  private clearLogin(name: string): void {
    this.logins.update((l) => {
      const copy = { ...l };
      delete copy[name];
      return copy;
    });
  }

  // ---- agente de cada papel

  assign(config: RoleConfig, adapterName: string): void {
    if (adapterName === config.adapterName) return;
    const previous = config.adapterName;
    this.configs.update((l) => l.map((c) => (c.id === config.id ? { ...c, adapterName } : c)));
    this.api.saveRoleConfig({ ...config, adapterName }).subscribe({
      next: () => {
        this.snack.open(`${ROLE_LABEL[config.role as AgentRole]} agora usa ${adapterName}.`, 'OK', { duration: 3000 });
        this.shared.refresh();
      },
      error: () => {
        this.configs.update((l) => l.map((c) => (c.id === config.id ? { ...c, adapterName: previous } : c)));
        this.snack.open('Não foi possível trocar o agente do papel.', 'OK', { duration: 5000 });
      },
    });
  }
}
