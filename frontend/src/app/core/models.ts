export type WorkItemState =
  | 'Backlog' | 'Todo' | 'InDevelopment' | 'TechLeadReview' | 'SecurityReview'
  | 'Qa' | 'AwaitingApproval' | 'Done' | 'Blocked' | 'Cancelled';
export type DemandStatus = 'New' | 'Refining' | 'InProgress' | 'Done' | 'Cancelled' | 'Failed';
export type AgentRole = 'ScrumMaster' | 'Dev' | 'TechLead' | 'Security' | 'Qa' | 'Human';
export type RunStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled';
export type Severity = 'Info' | 'Low' | 'Medium' | 'High' | 'Critical';

export interface Project {
  id: string;
  name: string;
  repositoryUrl: string;
  baseBranch: string;
  buildCommand: string;
  testCommand: string;
  autoMode: boolean;
  /** Onde os agentes e o git do projeto rodam; ausente/null = o ambiente padrão do servidor. */
  environmentId?: string | null;
  /** Perfil de stack: os agentes do projeto rodam num container dele (só em ambiente com host agent com Docker). */
  stackProfileId?: string | null;
  /** Papel (TechLead | Security) → ambiente onde ele roda no lugar do ambiente do projeto (revisa só pelo diff). */
  roleEnvironments?: Record<string, string | null> | null;
  /** Branch de destino dos pull requests das tarefas concluídas (ex.: develop); vazio = a branch base. */
  pullRequestBaseBranch?: string;
  /** Há um token do GitHub guardado para abrir PRs (o token nunca volta pela API). */
  hasGitHubToken?: boolean;
  /** Só na gravação: ausente mantém o token guardado; "" apaga; um valor substitui. */
  githubToken?: string;
  createdAt: string;
}
export type ProjectInput = Omit<Project, 'id' | 'createdAt'>;

export interface Demand {
  id: string;
  projectId: string;
  title: string;
  description: string;
  status: DemandStatus;
  createdAt: string;
}

export interface WorkItem {
  id: string;
  demandId: string;
  title: string;
  description: string;
  acceptanceCriteria: string;
  estimate: number;
  state: WorkItemState;
  reworkCount: number;
  branchName: string | null;
  blockedReason: string | null;
  /** A máquina do ambiente está fora do ar: a tarefa continua no estado atual e retoma sozinha quando ela voltar. */
  waitingReason?: string | null;
  /** Pull request aberto no GitHub com o branch desta tarefa. */
  pullRequestUrl?: string | null;
  pullRequestNumber?: number | null;
  dependsOnIds: string[];
  createdAt: string;
}

export interface RunSummary {
  id: string;
  demandId: string | null;
  workItemId: string | null;
  role: AgentRole;
  costUsd: number;
  durationMs: number;
  status: RunStatus;
  startedAt: string;
  finishedAt: string | null;
}
export interface Run extends RunSummary {
  prompt: string;
  output: string;
}

export interface Finding {
  id: string;
  workItemId: string;
  agentRunId: string | null;
  role: AgentRole;
  severity: Severity;
  file: string | null;
  line: number | null;
  message: string;
  resolved: boolean;
}

export interface RoleConfig {
  id: string;
  role: AgentRole;
  adapterName: string;
  systemPrompt: string;
  allowedTools: string;
  model: string;
  maxTurns: number;
}

// ---- eventos do hub /hubs/runs
export interface RunEvent {
  runId: string;
  projectId: string | null;
  demandId: string | null;
  workItemId: string | null;
  role: AgentRole;
  kind: string;
  text: string;
  at: string;
}
export interface WorkItemChanged {
  projectId: string;
  demandId: string;
  workItemId: string;
  state: WorkItemState;
  reworkCount: number;
  blockedReason: string | null;
  waitingReason?: string | null;
  branchName: string | null;
  at: string;
}
export interface DemandChanged {
  projectId: string;
  demandId: string;
  status: DemandStatus;
  at: string;
}

// ---- agentes (CLIs)
export interface AgentTestResult {
  ok: boolean;
  message: string;
  durationMs: number;
  costUsd: number;
  at: string;
}
export interface AgentInfo {
  name: string;
  displayName: string;
  installed: boolean;
  version: string | null;
  authenticated: boolean;
  authMethod: string | null;
  detail: string | null;
  supportsLogin: boolean;
  usedByRoles: AgentRole[];
  lastTest: AgentTestResult | null;
}
export type LoginState = 'WaitingForCode' | 'Succeeded' | 'Failed' | 'Cancelled';
export interface LoginSession {
  sessionId: string;
  url: string | null;
  state: LoginState;
  message: string | null;
}
// ---- integração (MCP)
export interface ApiTokenInfo {
  id: string;
  name: string;
  prefix: string;
  createdAt: string;
  lastUsedAt: string | null;
  revokedAt: string | null;
}
export interface CreatedApiToken {
  id: string;
  name: string;
  prefix: string;
  token: string;
}
export interface McpToolInfo {
  name: string;
  title: string;
  description: string;
  annotations?: { readOnlyHint?: boolean; destructiveHint?: boolean };
}
// ---- credenciais Git (chave SSH)
export interface GitSshStatus {
  supported: boolean;
  reason: string | null;
  hasKey: boolean;
  publicKey: string | null;
  fingerprint: string | null;
  createdAt: string | null;
  directory: string;
}
export interface GitAccessResult {
  ok: boolean;
  message: string;
}

// ---- importação do GitHub
export interface GitHubRepo {
  fullName: string;
  name: string;
  private: boolean;
  defaultBranch: string;
  description: string | null;
  cloneUrl: string;
  sshUrl: string;
  updatedAt: string | null;
  canAdmin: boolean;
}
export interface GitHubImportRequest {
  repository: string;
  token?: string;
  addDeployKey: boolean;
  buildCommand: string;
  testCommand: string;
  autoMode: boolean;
  environmentId?: string | null;
}
export interface GitHubImportResult {
  project: Project;
  cloned: boolean;
  deployKeyAdded: boolean;
  notes: string[];
  repository: GitHubRepo;
}
// ambientes de execução (máquinas com host agent)
export interface ExecutionEnvironment {
  id: string;
  name: string;
  url: string;
  workspaceRoot: string;
  tags: string;
  /** O token nunca é devolvido pela API: só se há um guardado. */
  hasToken: boolean;
  projectCount: number;
  createdAt: string;
  /** Tarefas simultâneas nesta máquina; as demais esperam vaga. */
  maxConcurrency: number;
}
export interface EnvironmentList {
  default: { mode: string; description: string };
  items: ExecutionEnvironment[];
}
export interface EnvironmentInput {
  name: string;
  url: string;
  /** Obrigatório ao criar; ao editar, vazio mantém o token guardado. */
  token: string;
  workspaceRoot: string;
  tags: string;
  maxConcurrency?: number;
}
export interface ToolCheck {
  tool: string;
  ok: boolean;
  detail: string;
}
export interface EnvironmentTest {
  reachable: boolean;
  error: string | null;
  workspaceRootExists: boolean;
  tools: ToolCheck[];
  /** O host agent habilitou containers por tarefa (HostAgent:Docker:Enabled). */
  containersEnabled?: boolean;
}

// perfis de stack (container por tarefa)
export interface StackProfile {
  id: string;
  name: string;
  description: string;
  image: string;
  dockerfile: string;
  network: 'bridge' | 'none';
  cpuLimit: number;
  memoryMb: number;
  builtIn: boolean;
  projectCount: number;
  createdAt: string;
}
export interface StackProfileInput {
  name: string;
  description: string;
  image: string;
  dockerfile: string;
  network: 'bridge' | 'none';
  cpuLimit: number;
  memoryMb: number;
}
export interface BuildImageResult {
  ok: boolean;
  image: string;
  built: boolean;
  log: string;
}

// carga por ambiente (painel da tela Ambientes)
export interface EnvironmentLoadRow {
  id: string | null;
  name: string;
  kind: 'default' | 'host';
  maxConcurrency: number;
  runningTasks: number;
  queuedTasks: number;
  runningAgents: number;
  projectCount: number;
}
export interface WaitingEntry {
  kind: 'task' | 'demand';
  id: string;
  title: string;
  project: string;
  reason: string;
  retryAt: string;
  attempts: number;
}
export interface EnvironmentLoadReport {
  environments: EnvironmentLoadRow[];
  waiting: WaitingEntry[];
}

export interface PullRequestResult {
  url: string;
  number: number;
  branch: string;
  baseBranch: string;
  alreadyExisted: boolean;
}
