import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatSnackBar } from '@angular/material/snack-bar';
import { ApiService } from '../core/api.service';
import { ROLE_LABEL } from '../core/labels';
import { RoleConfig } from '../core/models';

const ROLE_ICONS: Record<string, { color: string; svg: string }> = {
  Dev: { color: 'var(--accent)', svg: '<circle cx="9" cy="9" r="7" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M6 9h6M9 6v6" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>' },
  ScrumMaster: { color: 'var(--info)', svg: '<circle cx="9" cy="9" r="7" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M9 5v4l2.5 1.5" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>' },
  TechLead: { color: 'var(--warning)', svg: '<path d="M9 2l6.5 11H2.5L9 2z" fill="none" stroke="currentColor" stroke-width="1.5"/>' },
  Security: { color: 'var(--danger)', svg: '<path d="M9 2a2.5 2.5 0 00-2.5 2.5V6h5V4.5A2.5 2.5 0 009 2zM5 6V4.5a4 4 0 118 0V6h1.5A1.5 1.5 0 0116 7.5v7a1.5 1.5 0 01-1.5 1.5h-11A1.5 1.5 0 012 14.5v-7A1.5 1.5 0 013.5 6H5z"/>' },
  QA: { color: 'var(--success)', svg: '<circle cx="9" cy="9" r="6" fill="none" stroke="currentColor" stroke-width="1.5"/><path d="M6 9l2 2 4-4" stroke="currentColor" stroke-width="1.5" fill="none" stroke-linecap="round" stroke-linejoin="round"/>' },
};

@Component({
  selector: 'app-prompts-page',
  standalone: true,
  imports: [FormsModule],
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <div class="page-title">Prompts dos papéis</div>
          <div class="page-subtitle">System prompts versionados por papel</div>
        </div>
      </div>

      @for (c of configs(); track c.id) {
        <div class="accordion-item" [class.open]="openId() === c.id">
          <div class="accordion-header" (click)="togglePanel(c.id)">
            <svg width="18" height="18" viewBox="0 0 18 18" [style.color]="roleIcon(c.role).color" [innerHTML]="roleIcon(c.role).svg"></svg>
            <span>{{ roleLabel[c.role] }}</span>
            <span class="role-meta">{{ c.adapterName }} · {{ c.model || 'modelo padrão' }} · {{ c.maxTurns }} turnos</span>
            <span class="accordion-chevron">▾</span>
          </div>
          @if (openId() === c.id) {
            <div class="accordion-body">
              <form (ngSubmit)="save(c)" class="form-grid">
                <div class="form-group">
                  <div class="form-label">Modelo</div>
                  <input class="form-input" [name]="'model-' + c.id" [(ngModel)]="c.model" />
                </div>
                <div class="form-group">
                  <div class="form-label">Limite de turnos</div>
                  <input class="form-input" type="number" min="1" [name]="'turns-' + c.id" [(ngModel)]="c.maxTurns" required />
                </div>
                <div class="form-group">
                  <div class="form-label">Reasoning effort</div>
                  <select class="form-input" [name]="'effort-' + c.id" [(ngModel)]="c.reasoningEffort">
                    <option value="">Padrão do modelo</option>
                    <option value="low">low</option>
                    <option value="medium">medium</option>
                    <option value="high">high</option>
                  </select>
                </div>
                <div class="form-group">
                  <div class="form-label">Ferramentas permitidas</div>
                  <input class="form-input" [name]="'tools-' + c.id" [(ngModel)]="c.allowedTools" placeholder="Read,Glob,Grep" />
                </div>
                <div class="form-group full">
                  <div class="form-label">System prompt</div>
                  <textarea class="form-input" rows="8" style="font-size:11.5px" [name]="'prompt-' + c.id" [(ngModel)]="c.systemPrompt"></textarea>
                </div>
                <div style="margin-top:4px">
                  <button type="submit" class="btn btn-primary btn-sm" [disabled]="saving() === c.id">Salvar</button>
                </div>
              </form>
            </div>
          }
        </div>
      }
    </div>
  `,
  styles: `
    .accordion-item { background: var(--bg-1); border: 1px solid var(--border); border-radius: var(--radius); margin-bottom: 8px; overflow: hidden; }
    .accordion-header { padding: 14px 18px; cursor: pointer; display: flex; align-items: center; gap: 12px; font-size: 13px; font-weight: 600; transition: background var(--transition); }
    .accordion-header:hover { background: var(--bg-hover); }
    .accordion-chevron { margin-left: auto; color: var(--text-2); transition: transform .2s; }
    .accordion-item.open .accordion-chevron { transform: rotate(180deg); }
    .accordion-body { padding: 0 18px 18px; }
    .role-meta { font-size: 12px; color: var(--text-2); font-weight: 400; }
  `,
})
export class PromptsPage {
  private readonly api = inject(ApiService);
  private readonly snack = inject(MatSnackBar);

  readonly configs = signal<RoleConfig[]>([]);
  readonly saving = signal<string | null>(null);
  readonly openId = signal<string | null>(null);
  readonly roleLabel = ROLE_LABEL;

  constructor() {
    const order = Object.keys(ROLE_LABEL);
    this.api.roleConfigs().subscribe((c) =>
      this.configs.set(c.filter((x) => x.role !== 'Human').sort((a, b) => order.indexOf(a.role) - order.indexOf(b.role))));
  }

  roleIcon(role: string) {
    return ROLE_ICONS[role] ?? { color: 'var(--text-2)', svg: '' };
  }

  togglePanel(id: string): void {
    this.openId.set(this.openId() === id ? null : id);
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
