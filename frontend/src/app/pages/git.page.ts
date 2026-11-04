import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { GitAccessResult, GitSshStatus } from '../core/models';
import { ActionDialogComponent, BadgeComponent } from '../shared/shared';

@Component({
  selector: 'app-git-page',
  standalone: true,
  imports: [FormsModule, DatePipe, MatButtonModule, MatFormFieldModule, MatInputModule, BadgeComponent],
  template: `
    <div class="page">
      <h1>Credenciais Git</h1>
      <p class="muted">
        Para o AgentForge clonar repositórios <strong>privados</strong> por SSH. A chave é gerada no servidor e a parte privada
        <strong>nunca sai dele</strong>: você só copia a chave pública e a autoriza no GitHub (ou GitLab).
      </p>

      @if (status(); as s) {
        @if (!s.supported) {
          <section class="card" data-testid="unsupported">
            <app-badge label="usando o git do seu computador" tone="info" />
            <p>{{ s.reason }}</p>
          </section>
        } @else {
          <section class="card">
            <h2>Chave SSH do AgentForge</h2>
            @if (!s.hasKey) {
              <p class="muted">Ainda não há chave. Gere uma para poder usar URLs como <span class="mono">git&#64;github.com:organizacao/repositorio.git</span>.</p>
              <button mat-flat-button type="button" [disabled]="busy()" (click)="generate(false)">Gerar chave SSH</button>
            } @else {
              <div class="row">
                <app-badge label="chave gerada" tone="ok" />
                @if (s.createdAt) { <span class="muted small">em {{ s.createdAt | date: 'dd/MM/yyyy HH:mm' }}</span> }
                @if (s.fingerprint) { <span class="mono small">{{ s.fingerprint }}</span> }
              </div>

              <h3>Chave pública (copie e cole no GitHub)</h3>
              <pre class="block key" data-testid="public-key">{{ s.publicKey }}</pre>
              <div class="row">
                <button mat-flat-button type="button" (click)="copy(s.publicKey ?? '')">Copiar chave pública</button>
                <button mat-button type="button" [disabled]="busy()" (click)="regenerate()">Gerar outra…</button>
                <button mat-button type="button" [disabled]="busy()" (click)="remove()">Remover</button>
              </div>

              <h3>Como autorizar no GitHub</h3>
              <ol class="steps">
                <li>
                  <strong>Por repositório (recomendado):</strong> no repositório, <em>Settings → Deploy keys → Add deploy key</em>,
                  cole a chave e <strong>deixe "Allow write access" desmarcado</strong>. O AgentForge nunca faz push, então
                  leitura basta.
                </li>
                <li>
                  <strong>Para vários repositórios:</strong> em <em>Settings → SSH and GPG keys → New SSH key</em> da sua conta
                  (dá à chave o acesso da conta inteira).
                </li>
                <li>Volte aqui e use <em>Testar acesso</em> abaixo, com a URL SSH do repositório.</li>
              </ol>
            }
          </section>

          <section class="card">
            <h2>Testar acesso a um repositório</h2>
            <form class="row" (ngSubmit)="test()" #f="ngForm">
              <mat-form-field appearance="outline" class="url" subscriptSizing="dynamic">
                <mat-label>URL do repositório</mat-label>
                <input matInput name="repoUrl" [(ngModel)]="repoUrl" required placeholder="git&#64;github.com:organizacao/repositorio.git" />
              </mat-form-field>
              <button mat-flat-button type="submit" [disabled]="f.invalid || testing()">Testar acesso</button>
            </form>
            @if (testing()) { <p class="muted">Testando…</p> }
            @if (result(); as r) {
              <div class="result" [class.ok]="r.ok" [class.bad]="!r.ok" role="status" data-testid="access-result">
                <app-badge [label]="r.ok ? 'Acesso ok' : 'Sem acesso'" [tone]="r.ok ? 'ok' : 'bad'" /> {{ r.message }}
              </div>
            }
            <p class="muted small">
              Funciona também para URLs HTTPS públicas. Na primeira conexão com um servidor novo, a chave dele é registrada
              (<span class="mono">known_hosts</span>) e passa a ser exigida nas próximas.
            </p>
          </section>
        }
      } @else {
        <p class="muted">Carregando…</p>
      }
    </div>
  `,
  styles: `
    .card { background: #1b1b1b; border-radius: 10px; padding: .75rem 1rem 1rem; margin-bottom: 1rem; }
    .card h2 { margin: .25rem 0 .5rem; } .card h3 { margin: 1rem 0 .25rem; }
    .small { font-size: .85rem; }
    .key { word-break: break-all; white-space: pre-wrap; }
    .steps li { margin-bottom: .4rem; }
    .url { width: 34rem; max-width: 100%; }
    .result { margin-top: .75rem; padding: .5rem .75rem; border-radius: 8px; background: #151515; }
    .result.ok { border-left: 4px solid #3f9f58; } .result.bad { border-left: 4px solid #c25555; }
  `,
})
export class GitPage {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly status = signal<GitSshStatus | null>(null);
  readonly result = signal<GitAccessResult | null>(null);
  readonly busy = signal(false);
  readonly testing = signal(false);
  repoUrl = '';

  constructor() {
    this.api.gitSsh().subscribe((s) => this.status.set(s));
  }

  generate(overwrite: boolean): void {
    this.busy.set(true);
    this.api.generateGitSsh(overwrite).subscribe({
      next: (s) => {
        this.busy.set(false);
        this.status.set(s);
        this.result.set(null);
        this.snack.open('Chave gerada. Copie a pública e autorize-a no GitHub.', 'OK', { duration: 5000 });
      },
      error: (err) => {
        this.busy.set(false);
        this.snack.open(err?.error?.error ?? 'Não foi possível gerar a chave.', 'OK', { duration: 6000 });
      },
    });
  }

  regenerate(): void {
    this.dialog
      .open(ActionDialogComponent, {
        data: {
          title: 'Gerar outra chave',
          message: 'A chave atual deixará de funcionar: você precisará autorizar a nova no GitHub e remover a antiga de lá.',
          confirmLabel: 'Gerar outra',
        },
      })
      .afterClosed()
      .subscribe((ok) => ok && this.generate(true));
  }

  remove(): void {
    this.dialog
      .open(ActionDialogComponent, {
        data: { title: 'Remover chave', message: 'Repositórios privados por SSH deixarão de funcionar até gerar outra chave.', confirmLabel: 'Remover' },
      })
      .afterClosed()
      .subscribe((ok) => {
        if (!ok) return;
        this.api.deleteGitSsh().subscribe((s) => {
          this.status.set(s);
          this.result.set(null);
        });
      });
  }

  test(): void {
    if (!this.repoUrl.trim()) return;
    this.testing.set(true);
    this.result.set(null);
    this.api.testGitAccess(this.repoUrl.trim()).subscribe({
      next: (r) => {
        this.testing.set(false);
        this.result.set(r);
      },
      error: () => {
        this.testing.set(false);
        this.result.set({ ok: false, message: 'Não foi possível executar o teste.' });
      },
    });
  }

  async copy(text: string): Promise<void> {
    try {
      await navigator.clipboard.writeText(text);
      this.snack.open('Chave pública copiada.', 'OK', { duration: 2500 });
    } catch {
      this.snack.open('Não foi possível copiar; selecione e copie manualmente.', 'OK', { duration: 4000 });
    }
  }
}
