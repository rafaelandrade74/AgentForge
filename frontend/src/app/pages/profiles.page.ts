import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { BuildImageResult, ExecutionEnvironment, StackProfile, StackProfileInput } from '../core/models';
import { ActionDialogComponent, BadgeComponent } from '../shared/shared';

const EMPTY: StackProfileInput = { name: '', description: '', image: '', dockerfile: '', network: 'bridge', cpuLimit: 2, memoryMb: 4096 };

@Component({
  selector: 'app-profiles-page',
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, BadgeComponent],
  template: `
    <div class="page">
      <div class="row">
        <h1>Perfis de stack</h1>
        <span class="spacer"></span>
        <button mat-flat-button type="button" (click)="toggleForm()">{{ formOpen() ? 'Fechar' : 'Novo perfil' }}</button>
      </div>
      <p class="muted">
        Um perfil é a imagem Docker em que os agentes de um projeto rodam (Node 22, .NET 8, Python…). Cada tarefa sobe um container do
        perfil <strong>na máquina do ambiente</strong>, com só a pasta do projeto montada, sem privilégios, sem o socket do Docker e com
        limites de CPU e memória. Exige um ambiente cujo host agent tenha <span class="mono">HostAgent:Docker:Enabled</span> (Linux ou macOS).
        O projeto escolhe o perfil no formulário, junto com o ambiente.
      </p>

      @if (formOpen()) {
        <section class="card">
          <h2>{{ editingId() ? 'Editar perfil' : 'Novo perfil' }}</h2>
          <form (ngSubmit)="save()" #f="ngForm" class="grid">
            <mat-form-field appearance="outline">
              <mat-label>Nome</mat-label>
              <input matInput name="name" [(ngModel)]="draft.name" required placeholder="Go 1.23" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Descrição</mat-label>
              <input matInput name="description" [(ngModel)]="draft.description" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Imagem pronta (ex.: minha/imagem:1)</mat-label>
              <input matInput name="image" [(ngModel)]="draft.image" [disabled]="!!draft.dockerfile.trim()" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Rede</mat-label>
              <mat-select name="network" [(ngModel)]="draft.network">
                <mat-option value="bridge">Com rede</mat-option>
                <mat-option value="none">Sem rede</mat-option>
              </mat-select>
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>CPUs (0 = sem limite)</mat-label>
              <input matInput type="number" min="0" step="0.5" name="cpuLimit" [(ngModel)]="draft.cpuLimit" />
            </mat-form-field>
            <mat-form-field appearance="outline">
              <mat-label>Memória em MB (0 = sem limite)</mat-label>
              <input matInput type="number" min="0" step="256" name="memoryMb" [(ngModel)]="draft.memoryMb" />
            </mat-form-field>
            <mat-form-field appearance="outline" class="wide">
              <mat-label>…ou Dockerfile (construído na máquina do ambiente, sem arquivos de contexto)</mat-label>
              <textarea matInput rows="8" name="dockerfile" class="mono" [(ngModel)]="draft.dockerfile" [disabled]="!!draft.image.trim()"></textarea>
            </mat-form-field>
            <div class="row wide">
              <button mat-flat-button type="submit" [disabled]="f.invalid || saving()">Salvar</button>
              <button mat-button type="button" (click)="closeForm()">Cancelar</button>
            </div>
          </form>
          <p class="muted small">
            O agente roda <strong>dentro</strong> do container: a imagem precisa ter o <span class="mono">claude</span>, o <span class="mono">git</span> e
            <span class="mono">CLAUDE_CONFIG_DIR=/agent-config</span> (o login da máquina é montado aí). Imagens prontas precisam estar na lista
            <span class="mono">HostAgent:Docker:AllowedImages</span> do host agent; as construídas aqui (<span class="mono">agentforge/*</span>) sempre valem.
          </p>
        </section>
      }

      @if (environments().length > 0) {
        <div class="row build-env">
          <mat-form-field appearance="outline" subscriptSizing="dynamic">
            <mat-label>Construir imagens em</mat-label>
            <mat-select [value]="buildEnv()" (selectionChange)="buildEnv.set($event.value)" aria-label="Ambiente para construir imagens">
              @for (e of environments(); track e.id) { <mat-option [value]="e.id">{{ e.name }}</mat-option> }
            </mat-select>
          </mat-form-field>
        </div>
      }

      @for (p of profiles(); track p.id) {
        <section class="card" data-testid="profile">
          <div class="row">
            <h2>{{ p.name }}</h2>
            @if (p.builtIn) { <app-badge label="pronto" tone="info" /> }
            <app-badge [label]="p.network === 'none' ? 'sem rede' : 'com rede'" [tone]="p.network === 'none' ? 'warn' : 'idle'" />
            <span class="spacer"></span>
            <button mat-button type="button" [disabled]="!buildEnv() || building() === p.id" (click)="build(p, false)">Construir imagem</button>
            @if (!p.builtIn) {
              <button mat-button type="button" (click)="edit(p)">Editar</button>
              <button mat-button type="button" (click)="remove(p)">Excluir</button>
            }
          </div>
          <p class="muted small">
            {{ p.description }}
            {{ p.image ? 'Imagem ' + p.image : 'Dockerfile próprio' }} · {{ p.cpuLimit ? p.cpuLimit + ' CPU(s)' : 'CPU sem limite' }} ·
            {{ p.memoryMb ? p.memoryMb + ' MB' : 'memória sem limite' }} · {{ p.projectCount }} projeto(s)
          </p>
          @if (results()[p.id]; as r) {
            <div class="result" [class.ok]="r.ok" [class.bad]="!r.ok" role="status" data-testid="build-result">
              <app-badge [label]="!r.ok ? 'falhou' : r.built ? 'imagem construída' : 'imagem já existia'" [tone]="r.ok ? 'ok' : 'bad'" />
              <span class="mono small">{{ r.image }}</span>
              @if (r.log) { <pre class="block log">{{ r.log }}</pre> }
              @if (r.ok && !r.built) { <button mat-button type="button" (click)="build(p, true)">Reconstruir</button> }
            </div>
          }
        </section>
      } @empty {
        <p class="muted">Carregando…</p>
      }
      @if (building()) { <p class="muted">Construindo a imagem (a primeira vez pode levar vários minutos)…</p> }
    </div>
  `,
  styles: `
    .card { background: #1b1b1b; border-radius: 10px; padding: .75rem 1rem 1rem; margin-bottom: 1rem; }
    .card h2 { margin: .25rem 0; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr)); gap: .75rem 1rem; padding-top: .5rem; }
    .grid .wide { grid-column: 1 / -1; }
    .small { font-size: .85rem; }
    .result { margin-top: .5rem; padding: .5rem .75rem; border-radius: 8px; background: #151515; }
    .result.ok { border-left: 4px solid #3f9f58; } .result.bad { border-left: 4px solid #c25555; }
    .log { max-height: 16rem; overflow: auto; white-space: pre-wrap; }
    .build-env { margin-bottom: .75rem; }
  `,
})
export class ProfilesPage {
  private readonly api = inject(ApiService);
  private readonly dialog = inject(MatDialog);
  private readonly snack = inject(MatSnackBar);

  readonly profiles = signal<StackProfile[]>([]);
  readonly environments = signal<ExecutionEnvironment[]>([]);
  readonly buildEnv = signal<string | null>(null);
  readonly results = signal<Record<string, BuildImageResult>>({});
  readonly building = signal<string | null>(null);
  readonly formOpen = signal(false);
  readonly editingId = signal<string | null>(null);
  readonly saving = signal(false);
  draft: StackProfileInput = { ...EMPTY };

  constructor() {
    this.load();
    this.api.environments().subscribe((l) => {
      this.environments.set(l.items);
      this.buildEnv.set(l.items[0]?.id ?? null);
    });
  }

  private load(): void {
    this.api.stackProfiles().subscribe((p) => this.profiles.set(p));
  }

  toggleForm(): void {
    if (this.formOpen()) this.closeForm();
    else this.open(null, { ...EMPTY });
  }

  edit(p: StackProfile): void {
    this.open(p.id, {
      name: p.name, description: p.description, image: p.image, dockerfile: p.dockerfile, network: p.network, cpuLimit: p.cpuLimit, memoryMb: p.memoryMb,
    });
  }

  private open(id: string | null, draft: StackProfileInput): void {
    this.editingId.set(id);
    this.draft = draft;
    this.formOpen.set(true);
  }

  closeForm(): void {
    this.formOpen.set(false);
    this.editingId.set(null);
  }

  save(): void {
    this.saving.set(true);
    const id = this.editingId();
    const body: StackProfileInput = { ...this.draft, image: this.draft.image.trim(), cpuLimit: Number(this.draft.cpuLimit) || 0, memoryMb: Number(this.draft.memoryMb) || 0 };
    const call = id ? this.api.updateStackProfile(id, body) : this.api.createStackProfile(body);
    call.subscribe({
      next: () => {
        this.saving.set(false);
        this.closeForm();
        this.load();
      },
      error: (err) => {
        this.saving.set(false);
        const detail = err?.error?.error ?? Object.values<string[]>(err?.error?.errors ?? {})[0]?.[0];
        this.snack.open(detail ?? 'Não foi possível salvar o perfil.', 'OK', { duration: 6000 });
      },
    });
  }

  remove(p: StackProfile): void {
    this.dialog
      .open(ActionDialogComponent, { data: { title: 'Excluir perfil', message: `Excluir o perfil "${p.name}"?`, confirmLabel: 'Excluir' } })
      .afterClosed()
      .subscribe((ok) => {
        if (!ok) return;
        this.api.deleteStackProfile(p.id).subscribe({
          next: () => this.load(),
          error: (err) => this.snack.open(err?.error?.error ?? 'Não foi possível excluir.', 'OK', { duration: 6000 }),
        });
      });
  }

  build(p: StackProfile, force: boolean): void {
    const env = this.buildEnv();
    if (!env) return;
    this.building.set(p.id);
    this.api.buildStackProfile(p.id, env, force).subscribe({
      next: (r) => {
        this.building.set(null);
        this.results.update((x) => ({ ...x, [p.id]: r }));
      },
      error: (err) => {
        this.building.set(null);
        this.results.update((x) => ({ ...x, [p.id]: { ok: false, image: p.image, built: false, log: err?.error?.error ?? 'Não foi possível construir a imagem.' } }));
      },
    });
  }
}
