import { Component, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { MatButtonModule } from '@angular/material/button';
import { MatToolbarModule } from '@angular/material/toolbar';
import { AuthService } from '../auth/auth.service';
import { RealtimeService } from '../core/realtime.service';

@Component({
  selector: 'app-shell',
  standalone: true,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatButtonModule],
  template: `
    <mat-toolbar>
      <a routerLink="/" class="brand">AgentForge</a>
      <a mat-button routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Projetos</a>
      <a mat-button routerLink="/prompts" routerLinkActive="active">Prompts</a>
      <span class="spacer"></span>
      <span class="rt" [class.on]="realtime.status() === 'connected'" [title]="'Tempo real: ' + realtime.status()">●</span>
      <span class="user">{{ auth.username() }}</span>
      <button mat-button type="button" (click)="auth.logout()">Sair</button>
    </mat-toolbar>
    <router-outlet />
  `,
  styles: `
    mat-toolbar { gap: .5rem; border-bottom: 1px solid #2a2a2a; }
    .brand { font-weight: 700; margin-right: 1rem; text-decoration: none; color: inherit; }
    .spacer { flex: 1; }
    .active { background: #ffffff14; }
    .user { font-size: .9rem; opacity: .8; }
    .rt { color: #777; font-size: .8rem; }
    .rt.on { color: #5fd47a; }
  `,
})
export class ShellComponent {
  readonly auth = inject(AuthService);
  readonly realtime = inject(RealtimeService);
}
