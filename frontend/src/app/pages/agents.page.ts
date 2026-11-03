import { Component, computed, inject, signal } from '@angular/core';
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
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, BadgeComponent],
  template: `
    <div class="page">
      <h1>Agentes</h1>
      <p class="muted">
        Os papéis rodam no CLI de um agente instalado no servidor. Aqui você vê se ele já está conectado, testa na hora e,
        se precisar, entra pelo link — sem chave de API.
      </p>

      @if (environment(); as env) {
        <p class="env small" data-testid="environment">
          <app-badge [label]="env.mode === 'host' ? 'CLI no seu computador' : 'CLI neste servidor'" [tone]="env.mode === 'host' ? 'info' : 'idle'" />
          Os agentes rodam {{ env.description }}.
        </p>
      }

      @for (a of agents(); track a.name) {
        <section class="agent" [attr.aria-label]="a.displayName">
          <div class="row">
            <h2>{{ a.displayName }}</h2>
            @if (!a.installed) {
              <app-badge label="CLI não instalado" tone="bad" />
            } @else if (a.authenticated) {
              <app-badge label="Conectado" tone="ok" />
            } @else {
              <app-badge label="Não conectado" tone="warn" />
            }
            @if (a.version) { <span class="muted mono small">{{ a.version }}</span> }
            <span class="spacer"></span>
            @for (r of a.usedByRoles; track r) { <app-badge [label]="roleLabel[r]" /> }
          </div>

          @if (a.authenticated && a.detail) {
            <p class="muted">Conta: {{ a.detail }}@if (a.authMethod) { · {{ a.authMethod }} }</p>
          }
          @if (!a.installed) {
            <p class="bad-text">{{ a.detail }} Reconstrua a imagem da API com o CLI instalado.</p>
          }

          @if (a.installed && a.authenticated) {
            <div class="test">
              @if (testing()[a.name]) {
                <span class="muted">Testando a conexão…</span>
              } @else if (a.lastTest) {
                <app-badge [label]="a.lastTest.ok ? 'Teste passou' : 'Teste falhou'" [tone]="a.lastTest.ok ? 'ok' : 'bad'" />
                <span class="small">"{{ a.lastTest.message }}"</span>
                <span class="muted small">{{ duration(a.lastTest.durationMs) }} · {{ usd(a.lastTest.costUsd) }}</span>
              }
              <span class="spacer"></span>
              <button mat-stroked-button type="button" [disabled]="testing()[a.name]" (click)="test(a)">Testar de novo</button>
              @if (a.supportsLogin) {
                <button mat-button type="button" (click)="logout(a)">Desconectar</button>
              }
            </div>
          }

          @if (a.installed && !a.authenticated) {
            @if (a.supportsLogin) {
              @if (loginOf(a.name); as ui) {
                <div class="login" role="group" aria-label="Login por link">
                  @if (!ui.session && !ui.error) {
                    <span class="muted">Gerando o link de login…</span>
                  } @else if (ui.session?.state === 'WaitingForCode') {
                    <ol>
                      <li>
                        Abra o link e entre na sua conta:
                        @if (ui.session?.url; as url) {
                          <a [href]="url" target="_blank" rel="noopener noreferrer" class="mono small">Abrir página de login</a>
                        }
                      </li>
                      <li>Copie o código que a página mostrar e cole aqui:</li>
                    </ol>
                    <div class="row">
                      <mat-form-field appearance="outline" class="code">
                        <mat-label>Código</mat-label>
                        <input matInput [name]="'code-' + a.name" [ngModel]="ui.code" (ngModelChange)="setCode(a.name, $event)"
                          (keydown.enter)="sendCode(a)" autocomplete="off" />
                      </mat-form-field>
                      <button mat-flat-button type="button" [disabled]="!ui.code.trim() || ui.busy" (click)="sendCode(a)">Confirmar</button>
                      <button mat-button type="button" (click)="cancelLogin(a)">Cancelar</button>
                    </div>
                  } @else {
                    <button mat-flat-button type="button" (click)="startLogin(a)">Tentar de novo</button>
                  }
                  @if (ui.busy) { <span class="muted">Verificando…</span> }
                  @if (ui.error) { <p class="bad-text" role="alert">{{ ui.error }}</p> }
                </div>
              } @else {
                <p class="muted">Este agente ainda não está conectado.</p>
                <button mat-flat-button type="button" (click)="startLogin(a)">Conectar</button>
              }
            } @else {
              <p class="muted">Este agente não tem login pela aplicação; conecte-o direto no servidor.</p>
            }
          }
        </section>
      } @empty {
        <p class="muted">Carregando agentes…</p>
      }

      <h2>Agente de cada papel</h2>
      <div class="roles">
        @for (c of configs(); track c.id) {
          <div class="role">
            <span class="name">{{ roleLabel[c.role] }}</span>
            <mat-form-field appearance="outline" subscriptSizing="dynamic">
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
    .agent { background: #1b1b1b; border-radius: 10px; padding: .75rem 1rem 1rem; margin-bottom: 1rem; }
    .agent h2 { margin: .25rem 0; }
    .small { font-size: .85rem; }
    .env { display: flex; gap: .5rem; align-items: center; }
    .test { display: flex; gap: .75rem; align-items: center; flex-wrap: wrap; }
    .login { border-top: 1px solid #2c2c2c; margin-top: .5rem; padding-top: .5rem; }
    .login ol { margin: .25rem 0 .75rem; padding-left: 1.25rem; }
    .code { width: 28rem; max-width: 100%; }
    .bad-text { color: #ffb4b4; }
    .roles { display: grid; grid-template-columns: repeat(auto-fill, minmax(18rem, 1fr)); gap: .75rem 1.5rem; }
    .role { display: flex; align-items: center; gap: 1rem; }
    .role .name { width: 7rem; font-weight: 500; }
    .role mat-form-field { flex: 1; }
  `,
})
export class AgentsPage {
  private readonly api = inject(ApiService);
  private readonly shared = inject(AgentsService);
  private readonly snack = inject(MatSnackBar);

  readonly agents = computed(() => this.shared.agents() ?? []);
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
    this.api.agentEnvironment().subscribe({ next: (e) => this.environment.set(e), error: () => undefined });
    this.api.agents().subscribe({
      next: (list) => {
        this.shared.agents.set(list);
        // já conectado e ainda sem teste: testa agora (o servidor guarda o resultado, então não repete a cada visita)
        for (const a of list) if (a.installed && a.authenticated && !a.lastTest) this.test(a);
      },
      error: () => this.snack.open('Não foi possível carregar os agentes.', 'OK', { duration: 5000 }),
    });
  }

  loginOf(name: string): LoginUi | undefined {
    return this.logins()[name];
  }

  // ---- teste

  test(a: AgentInfo): void {
    this.testing.update((t) => ({ ...t, [a.name]: true }));
    this.api.testAgent(a.name).subscribe({
      next: (result) => this.finishTest(a.name, result),
      error: () => this.finishTest(a.name, { ok: false, message: 'Não foi possível executar o teste.', durationMs: 0, costUsd: 0, at: new Date().toISOString() }),
    });
  }

  private finishTest(name: string, result: AgentTestResult): void {
    this.testing.update((t) => ({ ...t, [name]: false }));
    this.shared.agents.update((list) => (list ?? []).map((x) => (x.name === name ? { ...x, lastTest: result } : x)));
  }

  // ---- login por link

  startLogin(a: AgentInfo): void {
    this.setLogin(a.name, { session: null, code: '', busy: false, error: null });
    this.api.startLogin(a.name).subscribe({
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
    this.api.sendLoginCode(a.name, ui.session.sessionId, ui.code.trim()).subscribe({
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
    if (ui?.session) this.api.cancelLogin(a.name, ui.session.sessionId).subscribe({ error: () => undefined });
    this.clearLogin(a.name);
  }

  private reloadAndTest(name: string): void {
    this.api.agent(name, true).subscribe((info) => {
      this.shared.replace(info);
      if (info.installed && info.authenticated) this.test(info);
    });
  }

  logout(a: AgentInfo): void {
    this.api.logoutAgent(a.name).subscribe({
      next: (info) => this.shared.replace(info),
      error: () => this.snack.open('Não foi possível desconectar.', 'OK', { duration: 5000 }),
    });
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
