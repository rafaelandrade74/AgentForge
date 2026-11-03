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
      { path: 'prompts', loadComponent: () => import('./pages/prompts.page').then((m) => m.PromptsPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
