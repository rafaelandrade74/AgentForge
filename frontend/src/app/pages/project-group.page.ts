import { Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { ApiService } from '../core/api.service';
import { Demand, Project, ProjectGroup, WorkItem } from '../core/models';
import { BadgeComponent } from '../shared/shared';

@Component({
  selector: 'app-project-group-page',
  standalone: true,
  imports: [RouterLink, MatButtonModule, MatCardModule, BadgeComponent],
  template: `
    <div class="page">
      @if (group(); as g) {
        <div class="header">
          <h1>{{ g.name }}</h1>
          @if (g.description) { <p class="muted">{{ g.description }}</p> }
        </div>

        <section>
          <h2>Projetos do grupo</h2>
          <div class="project-chips">
            @for (p of projects(); track p.id) {
              <a [routerLink]="['/projects', p.id]" class="chip">{{ p.name }}</a>
            } @empty { <p class="muted">Nenhum projeto neste grupo.</p> }
          </div>
        </section>

        <section>
          <div class="row">
            <h2>Demandas</h2>
            <span class="spacer"></span>
            <a mat-flat-button [routerLink]="['/projects', projects()[0]?.id, 'demands', 'new']"
               [queryParams]="{ projectGroupId: g.id }" [class.hidden]="projects().length === 0">Nova demanda</a>
          </div>
          @for (d of demands(); track d.id) {
            <mat-card class="demand-card">
              <mat-card-header>
                <mat-card-title><a [routerLink]="['/demands', d.id]">{{ d.title }}</a></mat-card-title>
              </mat-card-header>
              <mat-card-content>
                <app-badge [label]="d.status" [tone]="d.status === 'Done' ? 'ok' : 'info'" />
              </mat-card-content>
            </mat-card>
          } @empty { <p class="muted">Nenhuma demanda ainda.</p> }
        </section>
      }
    </div>
  `,
  styles: `
    .header { margin-bottom: 1rem; }
    .project-chips { display: flex; flex-wrap: wrap; gap: .5rem; margin: .5rem 0 1.5rem; }
    .chip { padding: .25rem .75rem; border-radius: 999px; background: var(--clr-surface-alt, #f0f0f0); text-decoration: none; color: inherit; font-size: .875rem; }
    .demand-card { margin-bottom: .75rem; }
    .demand-card mat-card-title a { text-decoration: none; color: inherit; }
    .hidden { display: none; }
  `,
})
export class ProjectGroupPage {
  private readonly api = inject(ApiService);
  private readonly route = inject(ActivatedRoute);

  readonly group = signal<ProjectGroup | null>(null);
  readonly projects = signal<Project[]>([]);
  readonly demands = signal<Demand[]>([]);

  constructor() {
    const id = this.route.snapshot.paramMap.get('id')!;
    this.api.projectGroup(id).subscribe(g => this.group.set(g));
    this.api.projects().subscribe(all => this.projects.set(all.filter(p => p.projectGroupId === id)));
    this.api.groupDemands(id).subscribe(d => this.demands.set(d));
  }
}
