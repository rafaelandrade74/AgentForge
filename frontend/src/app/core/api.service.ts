import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Demand, Finding, Project, ProjectInput, RoleConfig, Run, RunSummary, WorkItem } from './models';

/** Chamadas REST da API (o interceptor de auth anexa o token). */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // projetos
  projects = () => this.http.get<Project[]>('/api/projects');
  project = (id: string) => this.http.get<Project>(`/api/projects/${id}`);
  createProject = (p: ProjectInput) => this.http.post<Project>('/api/projects', p);
  updateProject = (id: string, p: ProjectInput) => this.http.put<Project>(`/api/projects/${id}`, p);
  deleteProject = (id: string) => this.http.delete<void>(`/api/projects/${id}`);

  // demandas
  demands = (projectId: string) => this.http.get<Demand[]>('/api/demands', { params: { projectId } });
  demand = (id: string) => this.http.get<Demand>(`/api/demands/${id}`);
  createDemand = (d: { projectId: string; title: string; description: string }) =>
    this.http.post<Demand>('/api/demands', d);
  startDemand = (id: string) => this.http.post<void>(`/api/demands/${id}/start`, null);
  approvePlan = (id: string) => this.http.post<void>(`/api/demands/${id}/approve-plan`, null);

  // tarefas
  workItemsOfProject = (projectId: string) => this.http.get<WorkItem[]>('/api/work-items', { params: { projectId } });
  workItemsOfDemand = (demandId: string) => this.http.get<WorkItem[]>('/api/work-items', { params: { demandId } });
  workItem = (id: string) => this.http.get<WorkItem>(`/api/work-items/${id}`);
  diff = (id: string): Observable<string> => this.http.get(`/api/work-items/${id}/diff`, { responseType: 'text' });
  approve = (id: string) => this.http.post<void>(`/api/work-items/${id}/approve`, null);
  reject = (id: string, comment: string) => this.http.post<void>(`/api/work-items/${id}/reject`, { comment });
  retry = (id: string) => this.http.post<void>(`/api/work-items/${id}/retry`, null);
  cancel = (id: string) => this.http.post<void>(`/api/work-items/${id}/cancel`, null);

  // execuções e apontamentos
  runsOfWorkItem = (workItemId: string) => this.http.get<RunSummary[]>('/api/agent-runs', { params: { workItemId } });
  runsOfDemand = (demandId: string) => this.http.get<RunSummary[]>('/api/agent-runs', { params: { demandId } });
  run = (id: string) => this.http.get<Run>(`/api/agent-runs/${id}`);
  findings = (workItemId: string) => this.http.get<Finding[]>('/api/review-findings', { params: { workItemId } });

  // papéis
  roleConfigs = () => this.http.get<RoleConfig[]>('/api/role-configs');
  saveRoleConfig = (c: RoleConfig) =>
    this.http.put<RoleConfig>(`/api/role-configs/${c.id}`, {
      role: c.role, adapterName: c.adapterName, systemPrompt: c.systemPrompt,
      allowedTools: c.allowedTools, model: c.model, maxTurns: c.maxTurns,
    });
}
