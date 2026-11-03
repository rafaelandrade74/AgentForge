import { DatePipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { ApiTokenInfo, CreatedApiToken, McpToolInfo } from '../core/models';
import { BadgeComponent } from '../shared/shared';

@Component({
  selector: 'app-integration-page',
  standalone: true,
  imports: [FormsModule, DatePipe, MatButtonModule, MatFormFieldModule, MatInputModule, BadgeComponent],
  template: `
    <div class="page">
      <h1>Integração (MCP)</h1>
      <p class="muted">
        O AgentForge é também um <strong>servidor MCP</strong>: o Claude Code (ou qualquer cliente MCP) pode criar demandas, acompanhar
        tarefas, ler o diff e aprovar ou rejeitar entregas por conversa. Nenhuma ferramenta faz push, merge nem apaga projetos.
      </p>

      <section class="card">
        <h2>Endereço</h2>
        <div class="row">
          <code class="mono url" data-testid="mcp-url">{{ mcpUrl }}</code>
          <button mat-stroked-button type="button" (click)="copy(mcpUrl)">Copiar</button>
        </div>
      </section>

      <section class="card">
        <h2>Tokens de acesso</h2>
        <p class="muted small">
          Um token vale só no <span class="mono">/mcp</span> (não abre o resto da API) e pode ser revogado a qualquer momento.
          O valor aparece uma única vez, ao criar.
        </p>
        <form class="row" (ngSubmit)="create()" #f="ngForm">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Nome do token</mat-label>
            <input matInput name="tokenName" [(ngModel)]="name" required maxlength="100" placeholder="Claude Code no meu notebook" />
          </mat-form-field>
          <button mat-flat-button type="submit" [disabled]="f.invalid || busy()">Criar token</button>
        </form>

        @if (created(); as c) {
          <div class="created" role="status" data-testid="created-token">
            <strong>Copie agora: este valor não será mostrado de novo.</strong>
            <div class="row">
              <code class="mono token">{{ c.token }}</code>
              <button mat-stroked-button type="button" (click)="copy(c.token)">Copiar token</button>
            </div>
            <h3>Claude Code</h3>
            <pre class="block" data-testid="cmd-claude">{{ claudeCommand() }}</pre>
            <button mat-button type="button" (click)="copy(claudeCommand())">Copiar comando</button>
            <h3>Outros clientes (arquivo <span class="mono">.mcp.json</span>)</h3>
            <pre class="block" data-testid="cmd-json">{{ jsonConfig() }}</pre>
            <button mat-button type="button" (click)="copy(jsonConfig())">Copiar JSON</button>
          </div>
        }

        <table class="tokens">
          <thead><tr><th>Nome</th><th>Token</th><th>Criado</th><th>Último uso</th><th></th></tr></thead>
          <tbody>
            @for (t of tokens(); track t.id) {
              <tr [class.revoked]="t.revokedAt">
                <td>{{ t.name }}</td>
                <td class="mono">{{ t.prefix }}…</td>
                <td>{{ t.createdAt | date: 'dd/MM/yyyy HH:mm' }}</td>
                <td>{{ t.lastUsedAt ? (t.lastUsedAt | date: 'dd/MM HH:mm') : 'nunca' }}</td>
                <td>
                  @if (t.revokedAt) {
                    <app-badge label="revogado" tone="idle" />
                  } @else {
                    <button mat-button type="button" (click)="revoke(t)">Revogar</button>
                  }
                </td>
              </tr>
            } @empty {
              <tr><td colspan="5" class="muted">Nenhum token ainda.</td></tr>
            }
          </tbody>
        </table>
      </section>

      <section class="card">
        <h2>Ferramentas oferecidas ({{ tools().length }})</h2>
        <div class="tools">
          @for (t of tools(); track t.name) {
            <div class="tool">
              <div class="row">
                <span class="mono name">{{ t.name }}</span>
                @if (t.annotations?.readOnlyHint) { <app-badge label="só leitura" tone="ok" /> }
                @if (t.annotations?.destructiveHint) { <app-badge label="destrutiva" tone="bad" /> }
              </div>
              <div class="muted small">{{ t.description }}</div>
            </div>
          }
        </div>
      </section>
    </div>
  `,
  styles: `
    .card { background: #1b1b1b; border-radius: 10px; padding: .75rem 1rem 1rem; margin-bottom: 1rem; }
    .card h2 { margin: .25rem 0 .5rem; }
    .small { font-size: .85rem; }
    .url, .token { background: #0d0d0d; padding: .4rem .6rem; border-radius: 6px; word-break: break-all; }
    .created { background: #1d2a1d; border: 1px solid #2f5a2f; border-radius: 8px; padding: .75rem 1rem; margin: .75rem 0; }
    .created h3 { margin: .75rem 0 .25rem; }
    .tokens { width: 100%; border-collapse: collapse; margin-top: .75rem; }
    .tokens th, .tokens td { text-align: left; padding: .4rem .5rem; border-bottom: 1px solid #2a2a2a; }
    .tokens tr.revoked { opacity: .55; }
    .tools { display: grid; grid-template-columns: repeat(auto-fill, minmax(22rem, 1fr)); gap: .5rem .75rem; }
    .tool { background: #151515; border-radius: 8px; padding: .5rem .7rem; }
    .name { font-weight: 600; }
  `,
})
export class IntegrationPage {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);

  readonly mcpUrl = `${window.location.origin}/mcp`;
  readonly tokens = signal<ApiTokenInfo[]>([]);
  readonly tools = signal<McpToolInfo[]>([]);
  readonly created = signal<CreatedApiToken | null>(null);
  readonly busy = signal(false);
  name = '';

  readonly claudeCommand = computed(() =>
    `claude mcp add --transport http agentforge ${this.mcpUrl} --header "Authorization: Bearer ${this.created()?.token ?? '<token>'}"`);

  readonly jsonConfig = computed(() => JSON.stringify(
    { mcpServers: { agentforge: { type: 'http', url: this.mcpUrl, headers: { Authorization: `Bearer ${this.created()?.token ?? '<token>'}` } } } },
    null, 2));

  constructor() {
    this.loadTokens();
    this.api.mcpTools().subscribe({ next: (r) => this.tools.set(r.result.tools), error: () => undefined });
  }

  private loadTokens(): void {
    this.api.apiTokens().subscribe((t) => this.tokens.set(t));
  }

  create(): void {
    const name = this.name.trim();
    if (!name) return;
    this.busy.set(true);
    this.api.createApiToken(name).subscribe({
      next: (token) => {
        this.busy.set(false);
        this.created.set(token);
        this.name = '';
        this.loadTokens();
      },
      error: () => {
        this.busy.set(false);
        this.snack.open('Não foi possível criar o token.', 'OK', { duration: 5000 });
      },
    });
  }

  revoke(t: ApiTokenInfo): void {
    this.api.revokeApiToken(t.id).subscribe({
      next: () => {
        if (this.created()?.id === t.id) this.created.set(null);
        this.loadTokens();
      },
      error: () => this.snack.open('Não foi possível revogar.', 'OK', { duration: 5000 }),
    });
  }

  async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.snack.open('Copiado.', 'OK', { duration: 2000 });
    } catch {
      this.snack.open('Não foi possível copiar; selecione e copie manualmente.', 'OK', { duration: 4000 });
    }
  }
}
