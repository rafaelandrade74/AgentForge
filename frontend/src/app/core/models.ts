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