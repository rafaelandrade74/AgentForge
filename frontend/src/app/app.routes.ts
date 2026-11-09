import { Routes } from '@angular/router';
import { authGuard } from './auth/auth.guard';
import { LoginComponent } from './login/login.component';
import { ShellComponent } from './shell/shell.component';

export const routes: Routes = [
  { path: 'login', component: LoginComponent },
  {
    path: '',
    component: ShellComponent,
    canActivate: [authGuard],
    children: [
      { path: '', loadComponent: () => import('./pages/projects.page').then((m) => m.ProjectsPage) },
      { path: 'projects/:id', loadComponent: () => import('./pages/project.page').then((m) => m.ProjectPage) },
      { path: 'projects/:id/demands/new', loadComponent: () => import('./pages/new-demand.page').then((m) => m.NewDemandPage) },
      { path: 'demands/:id', loadComponent: () => import('./pages/demand.page').then((m) => m.DemandPage) },
      { path: 'tasks/:id', loadComponent: () => import('./pages/task.page').then((m) => m.TaskPage) },
      { path: 'approvals', loadComponent: () => import('./pages/approvals.page').then((m) => m.ApprovalsPage) },
      { path: 'live', loadComponent: () => import('./pages/live.page').then((m) => m.LivePage) },
      { path: 'chat', loadComponent: () => import('./pages/chat.page').then((m) => m.ChatPage) },
      { path: 'chat/:id', loadComponent: () => import('./pages/chat.page').then((m) => m.ChatPage) },
      { path: 'agents', loadComponent: () => import('./pages/agents.page').then((m) => m.AgentsPage) },
      { path: 'profiles', loadComponent: () => import('./pages/profiles.page').then((m) => m.ProfilesPage) },
      { path: 'environments', loadComponent: () => import('./pages/environments.page').then((m) => m.EnvironmentsPage) },
      { path: 'git', loadComponent: () => import('./pages/git.page').then((m) => m.GitPage) },
      { path: 'integration', loadComponent: () => import('./pages/integration.page').then((m) => m.IntegrationPage) },
      { path: 'prompts', loadComponent: () => import('./pages/prompts.page').then((m) => m.PromptsPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
