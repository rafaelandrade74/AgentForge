import { Component, inject, input, output, signal } from '@angular/core';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { ApiService } from '../core/api.service';
import { ExecutionEnvironment } from '../core/models';

/**
 * Escolha da máquina em que a tela trabalha (chave Git, login do agente). Só aparece quando há ambientes cadastrados:
 * sem eles só existe o padrão do servidor e a tela fica como sempre foi. Valor nulo = padrão do servidor.
 */
@Component({
  selector: 'app-environment-select',
  standalone: true,
  imports: [MatFormFieldModule, MatSelectModule],
  template: `
    @if (environments().length > 0) {
      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="env-select" data-testid="env-select">
        <mat-label>Ambiente</mat-label>
        <mat-select [value]="value() ?? ''" (selectionChange)="changed.emit($event.value || null)" aria-label="Ambiente">
          <mat-option value="">Padrão do servidor</mat-option>
          @for (e of environments(); track e.id) { <mat-option [value]="e.id">{{ e.name }}</mat-option> }
        </mat-select>
      </mat-form-field>
    }
  `,
  styles: `.env-select { width: 22rem; max-width: 100%; margin-bottom: 1rem; }`,
})
export class EnvironmentSelectComponent {
  private readonly api = inject(ApiService);

  readonly value = input<string | null>(null);
  readonly changed = output<string | null>();
  readonly environments = signal<ExecutionEnvironment[]>([]);

  constructor() {
    this.api.environments().subscribe({ next: (l) => this.environments.set(l.items), error: () => undefined });
  }
}
