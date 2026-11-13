import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentInfo, AgentTestResult, Approval, ApiTokenInfo, CreatedApiToken, Demand, EnvironmentInput, EnvironmentList, EnvironmentLoadReport, EnvironmentTest, ExecutionEnvironment, Finding, GitAccessResult, GitHubImportRequest, GitHubImportResult, GitHubRepo, GitSshStatus, LoginSession, McpToolInfo, BuildImageResult, PullRequestResult, PushResult, LiveAgents, ChatSession, ChatDetail, ChatMessage, ProposalCreated, Project, ProjectInput, RoleConfig, Run, RunSummary, StackProfile, StackProfileInput, UsageLimitInfo, WorkItem,
} from './models';

/** Parâmetros de query com o ambiente escolhido; sem ambiente (padrão do servidor) a URL fica exatamente como antes. */
function envParams(environmentId?: string | null, extra: Record<string, string> = {}): Record<string, string> {
  return environmentId ? { ...extra, environmentId } : extra;
}

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
  openPullRequest = (workItemId: string, baseBranch?: string) =>
    this.http.post<PullRequestResult>(`/api/work-items/${workItemId}/pull-request`, { baseBranch: baseBranch || null });
  liveAgents = () => this.http.get<LiveAgents>('/api/live/agents');
  chats = () => this.http.get<ChatSession[]>('/api/chats');
  chat = (id: string) => this.http.get<ChatDetail>(`/api/chats/${id}`);
  createChat = (body: { projectId: string; environmentId: string | null; adapterName?: string }) =>
    this.http.post<ChatSession>('/api/chats', body);
  sendChat = (id: string, text: string) => this.http.post<ChatMessage>(`/api/chats/${id}/messages`, { text });
  deleteChat = (id: string) => this.http.delete<void>(`/api/chats/${id}`);
  createProposalDemand = (chatId: string, messageId: string, index: number, start: boolean) =>
    this.http.post<ProposalCreated>(`/api/chats/${chatId}/messages/${messageId}/demands/${index}`, { start });
  pushBranch = (workItemId: string) => this.http.post<PushResult>(`/api/work-items/${workItemId}/push`, null);
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

  // agentes (CLIs): status, teste e login por link
  // environmentId (opcional) escolhe a máquina (cada uma tem o seu login); sem ele vale o ambiente padrão do servidor
  agents = (environmentId?: string | null) => this.http.get<AgentInfo[]>('/api/agents', { params: envParams(environmentId) });
  agentEnvironment = (environmentId?: string | null) =>
    this.http.get<{ mode: 'local' | 'host'; description: string }>('/api/agents/environment', { params: envParams(environmentId) });
  agent = (name: string, refresh = false, environmentId?: string | null) =>
    this.http.get<AgentInfo>(`/api/agents/${name}`, { params: envParams(environmentId, { refresh: String(refresh) }) });
  testAgent = (name: string, environmentId?: string | null) =>
    this.http.post<AgentTestResult>(`/api/agents/${name}/test`, null, { params: envParams(environmentId) });
  startLogin = (name: string, environmentId?: string | null) =>
    this.http.post<LoginSession>(`/api/agents/${name}/login`, null, { params: envParams(environmentId) });
  sendLoginCode = (name: string, sessionId: string, code: string, environmentId?: string | null) =>
    this.http.post<LoginSession>(`/api/agents/${name}/login/${sessionId}/code`, { code }, { params: envParams(environmentId) });
  cancelLogin = (name: string, sessionId: string, environmentId?: string | null) =>
    this.http.delete<LoginSession>(`/api/agents/${name}/login/${sessionId}`, { params: envParams(environmentId) });
  logoutAgent = (name: string, environmentId?: string | null) =>
    this.http.post<AgentInfo>(`/api/agents/${name}/logout`, null, { params: envParams(environmentId) });

  // integração (MCP): tokens de acesso e catálogo de ferramentas
  apiTokens = () => this.http.get<ApiTokenInfo[]>('/api/tokens');
  createApiToken = (name: string) => this.http.post<CreatedApiToken>('/api/tokens', { name });
  revokeApiToken = (id: string) => this.http.delete<void>(`/api/tokens/${id}`);
  mcpTools = () =>
    this.http.post<{ result: { tools: McpToolInfo[] } }>('/mcp', { jsonrpc: '2.0', id: 1, method: 'tools/list' });

  // credenciais Git (chave SSH gerada no servidor)
  gitSsh = (environmentId?: string | null) => this.http.get<GitSshStatus>('/api/git/ssh', { params: envParams(environmentId) });
  generateGitSsh = (overwrite = false, environmentId?: string | null) =>
    this.http.post<GitSshStatus>('/api/git/ssh/generate', { overwrite }, { params: envParams(environmentId) });
  deleteGitSsh = (environmentId?: string | null) => this.http.delete<GitSshStatus>('/api/git/ssh', { params: envParams(environmentId) });
  testGitAccess = (repositoryUrl: string, environmentId?: string | null) =>
    this.http.post<GitAccessResult>('/api/git/ssh/test', { repositoryUrl }, { params: envParams(environmentId) });

  // ambientes de execução
  environments = () => this.http.get<EnvironmentList>('/api/environments');
  environmentLoad = () => this.http.get<EnvironmentLoadReport>('/api/environments/load');
  approvals = () => this.http.get<Approval[]>('/api/approvals');
  usageLimits = () => this.http.get<UsageLimitInfo[]>('/api/environments/usage-limits');

  // perfis de stack (container por tarefa)
  stackProfiles = () => this.http.get<StackProfile[]>('/api/stack-profiles');
  createStackProfile = (p: StackProfileInput) => this.http.post<StackProfile>('/api/stack-profiles', p);
  updateStackProfile = (id: string, p: StackProfileInput) => this.http.put<StackProfile>(`/api/stack-profiles/${id}`, p);
  deleteStackProfile = (id: string) => this.http.delete<void>(`/api/stack-profiles/${id}`);
  buildStackProfile = (id: string, environmentId: string, force = false) =>
    this.http.post<BuildImageResult>(`/api/stack-profiles/${id}/build`, null, { params: { environmentId, force: String(force) } });
  createEnvironment = (input: EnvironmentInput) => this.http.post<ExecutionEnvironment>('/api/environments', input);
  updateEnvironment = (id: string, input: EnvironmentInput) => this.http.put<ExecutionEnvironment>(`/api/environments/${id}`, input);
  deleteEnvironment = (id: string) => this.http.delete<void>(`/api/environments/${id}`);
  /** id = o do ambiente, ou 'default' para o padrão do servidor. */
  testEnvironment = (id: string) => this.http.post<EnvironmentTest>(`/api/environments/${id}/test`, {});

  // importação do GitHub (o token vai só nestas chamadas; o servidor não o guarda)
  githubRepos = (token: string) => this.http.post<GitHubRepo[]>('/api/github/repos', { token });
  githubImport = (req: GitHubImportRequest) => this.http.post<GitHubImportResult>('/api/github/import', req);

  // papéis
  roleConfigs = () => this.http.get<RoleConfig[]>('/api/role-configs');
  saveRoleConfig = (c: RoleConfig) =>
    this.http.put<RoleConfig>(`/api/role-configs/${c.id}`, {
      role: c.role, adapterName: c.adapterName, systemPrompt: c.systemPrompt,
      allowedTools: c.allowedTools, model: c.model, maxTurns: c.maxTurns,
      reasoningEffort: c.reasoningEffort,
    });
}
