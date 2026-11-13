import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { ROLE_LABEL } from '../core/labels';
import { RoleConfig } from '../core/models';

@Component({
  selector: 'app-prompts-page',
  standalone: true,
  imports: [FormsModule, MatButtonModule, MatExpansionModule, MatFormFieldModule, MatInputModule, MatSelectModule],
  template: `
    <div class="page">
      <h1>Prompts dos papéis</h1>
      <p class="muted">
        O agente de cada papel é escolhido na tela <a href="/agents">Agentes</a>. O prompt de sistema é anexado a cada execução do papel. A saída de todo agente precisa terminar com um bloco JSON no
        formato exigido; o orquestrador acrescenta esse formato ao pedido, mas mantenha-o também no prompt.
      </p>
      <mat-accordion multi>
        @for (c of configs(); track c.id) {
          <mat-expansion-panel>
            <mat-expansion-panel-header>
              <mat-panel-title>{{ roleLabel[c.role] }}</mat-panel-title>
              <mat-panel-description>{{ c.adapterName }} · {{ c.model || 'modelo padrão' }} · {{ c.maxTurns }} turnos</mat-panel-description>
            </mat-expansion-panel-header>
            <form (ngSubmit)="save(c)" class="form">
              <div class="grid">
                <mat-form-field appearance="outline">
                  <mat-label>Modelo</mat-label>
                  <input matInput [name]="'model-' + c.id" [(ngModel)]="c.model" />
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Limite de turnos</mat-label>
                  <input matInput type="number" min="1" [name]="'turns-' + c.id" [(ngModel)]="c.maxTurns" required />
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Reasoning effort</mat-label>
                  <mat-select [name]="'effort-' + c.id" [(ngModel)]="c.reasoningEffort">
                    <mat-option value="">Padrão do modelo</mat-option>
                    <mat-option value="low">Low</mat-option>
                    <mat-option value="medium">Medium</mat-option>
                    <mat-option value="high">High</mat-option>
                  </mat-select>
                </mat-form-field>
                <mat-form-field appearance="outline">
                  <mat-label>Ferramentas permitidas</mat-label>
                  <input matInput [name]="'tools-' + c.id" [(ngModel)]="c.allowedTools" placeholder="Read,Glob,Grep" />
                </mat-form-field>
              </div>
              <mat-form-field appearance="outline" class="full">
                <mat-label>Prompt de sistema</mat-label>
                <textarea matInput rows="14" class="mono" [name]="'prompt-' + c.id" [(ngModel)]="c.systemPrompt"></textarea>
              </mat-form-field>
              <button mat-flat-button type="submit" [disabled]="saving() === c.id">Salvar {{ roleLabel[c.role] }}</button>
            </form>
          </mat-expansion-panel>
        }
      </mat-accordion>
    </div>
  `,
  styles: `
    .form { display: flex; flex-direction: column; gap: .5rem; padding-top: .75rem; }
    .grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: .5rem 1rem; }
    .full { width: 100%; }
    textarea { font-family: ui-monospace, Menlo, Consolas, monospace; font-size: .85rem; }
  `,
})
export class PromptsPage {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);

  readonly configs = signal<RoleConfig[]>([]);
  readonly saving = signal<string | null>(null);
  readonly roleLabel = ROLE_LABEL;

  constructor() {
    // na ordem do fluxo (a API ordena pelo nome do enum)
    const order = Object.keys(ROLE_LABEL);
    this.api.roleConfigs().subscribe((c) =>
      this.configs.set(c.filter((x) => x.role !== 'Human').sort((a, b) => order.indexOf(a.role) - order.indexOf(b.role))));
  }

  save(c: RoleConfig): void {
    this.saving.set(c.id);
    this.api.saveRoleConfig(c).subscribe({
      next: () => {
        this.saving.set(null);
        this.snack.open(`${ROLE_LABEL[c.role]} salvo. Vale a partir da próxima execução.`, 'OK', { duration: 4000 });
      },
      error: () => {
        this.saving.set(null);
        this.snack.open('Não foi possível salvar.', 'OK', { duration: 5000 });
      },
    });
  }
}
