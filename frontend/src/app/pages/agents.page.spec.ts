import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { AgentInfo, ExecutionEnvironment, RoleConfig } from '../core/models';
import { AgentsService } from '../core/agents.service';
import { AgentsPage } from './agents.page';

const agent = (over: Partial<AgentInfo> = {}): AgentInfo => ({
  name: 'claude-code', displayName: 'Claude Code', installed: true, version: '2.1.316 (Claude Code)', authenticated: false,
  authMethod: 'none', detail: 'Não conectado.', supportsLogin: true, usedByRoles: ['Dev', 'Qa'], lastTest: null, ...over,
});

const roleConfig = (role: RoleConfig['role']): RoleConfig => ({
  id: `id-${role}`, role, adapterName: 'claude-code', systemPrompt: '', allowedTools: '', model: '', maxTurns: 20,
});

describe('AgentsPage', () => {
  let fixture: ComponentFixture<AgentsPage>;
  let http: HttpTestingController;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const click = (label: string) => {
    const button = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button, a'))
      .find((b) => b.textContent?.trim().startsWith(label)) as HTMLElement | undefined;
    expect(button).withContext(`botão "${label}"`).toBeDefined();
    button!.click();
    fixture.detectChanges();
  };

  function open(agents: AgentInfo[], environments: ExecutionEnvironment[] = []): void {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AgentsPage);
    fixture.detectChanges();
    http.expectOne('/api/role-configs').flush([roleConfig('Dev'), roleConfig('Qa')]);
    http.expectOne('/api/agents/environment').flush({ mode: 'host', description: 'no computador, pelo host agent (host.docker.internal:8787, Windows)' });
    http.expectOne('/api/agents').flush(agents);
    http.expectOne('/api/environments').flush({ default: { mode: 'host', description: '' }, items: environments });
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('mostra onde os CLIs rodam', () => {
    open([agent({ authenticated: true, lastTest: { ok: true, message: 'OK', durationMs: 1, costUsd: 0, at: '' } })]);

    expect(text()).toContain('CLI no seu computador');
    expect(text()).toContain('pelo host agent');
  });

  it('já conectado: testa sozinho, sem pedir login nem chave, e mostra o resultado', () => {
    open([agent({ authenticated: true, authMethod: 'claude.ai', detail: 'voce@exemplo.com · max' })]);

    expect(text()).toContain('Conectado');
    expect(text()).toContain('voce@exemplo.com');
    expect(text()).not.toContain('Conectar');
    http.expectOne({ method: 'POST', url: '/api/agents/claude-code/test' })
      .flush({ ok: true, message: 'OK', durationMs: 2300, costUsd: 0.0123, at: '' });
    fixture.detectChanges();

    expect(text()).toContain('Teste passou');
    expect(text()).toContain('"OK"');
    expect(text()).toContain('2 s');
  });

  it('com teste recente guardado pelo servidor, não repete a chamada paga', () => {
    open([agent({ authenticated: true, lastTest: { ok: true, message: 'OK', durationMs: 900, costUsd: 0.01, at: '' } })]);

    http.expectNone('/api/agents/claude-code/test');
    expect(text()).toContain('Teste passou');
  });

  it('não conectado: conduz o login por link, recebe o código e conecta', () => {
    open([agent()]);
    expect(text()).toContain('Não conectado');

    click('Conectar');
    http.expectOne({ method: 'POST', url: '/api/agents/claude-code/login' })
      .flush({ sessionId: 's1', url: 'https://claude.com/cai/oauth/authorize?code=true', state: 'WaitingForCode', message: null });
    fixture.detectChanges();

    const link = (fixture.nativeElement as HTMLElement).querySelector('a[href^="https://claude.com"]') as HTMLAnchorElement;
    expect(link.target).toBe('_blank');
    expect(link.rel).toContain('noopener');

    const input = (fixture.nativeElement as HTMLElement).querySelector('input[name="code-claude-code"]') as HTMLInputElement;
    input.value = '  codigo-abc#123  ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    click('Confirmar');

    const req = http.expectOne({ method: 'POST', url: '/api/agents/claude-code/login/s1/code' });
    expect(req.request.body).toEqual({ code: 'codigo-abc#123' });
    req.flush({ sessionId: 's1', url: null, state: 'Succeeded', message: 'Conectado: voce@exemplo.com' });
    http.expectOne((r) => r.url === '/api/agents/claude-code' && r.params.get('refresh') === 'true')
      .flush(agent({ authenticated: true, detail: 'voce@exemplo.com' }));
    http.expectOne({ method: 'POST', url: '/api/agents/claude-code/test' })
      .flush({ ok: true, message: 'OK', durationMs: 1000, costUsd: 0.01, at: '' });
    fixture.detectChanges();

    expect(text()).toContain('Conectado');
    expect(text()).toContain('Teste passou');
    expect(text()).not.toContain('Cole o código');
  });

  it('código errado: mostra o motivo e permite tentar de novo', () => {
    open([agent()]);
    click('Conectar');
    http.expectOne('/api/agents/claude-code/login')
      .flush({ sessionId: 's1', url: 'https://claude.com/x', state: 'WaitingForCode', message: null });
    fixture.detectChanges();

    const input = (fixture.nativeElement as HTMLElement).querySelector('input[name="code-claude-code"]') as HTMLInputElement;
    input.value = 'errado';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    click('Confirmar');
    http.expectOne('/api/agents/claude-code/login/s1/code')
      .flush({ sessionId: 's1', url: 'https://claude.com/x', state: 'Failed', message: 'Login não concluído. Invalid code' });
    fixture.detectChanges();

    expect(text()).toContain('Login não concluído');
    expect(text()).toContain('Tentar de novo');
  });

  it('CLI não instalado: avisa e não oferece login', () => {
    open([agent({ installed: false, version: null, detail: "CLI 'claude' não encontrado." })]);

    expect(text()).toContain('CLI não instalado');
    expect(text()).not.toContain('Conectar');
  });

  it('troca o agente de um papel e desfaz se a API recusar', () => {
    open([agent({ authenticated: true, lastTest: { ok: true, message: 'OK', durationMs: 1, costUsd: 0, at: '' } }),
      agent({ name: 'outro', displayName: 'Outro', authenticated: true, usedByRoles: [], lastTest: { ok: true, message: 'OK', durationMs: 1, costUsd: 0, at: '' } })]);
    const page = fixture.componentInstance;
    const dev = page.configs().find((c) => c.role === 'Dev')!;

    page.assign(dev, 'outro');
    const put = http.expectOne({ method: 'PUT', url: `/api/role-configs/${dev.id}` });
    expect(put.request.body.adapterName).toBe('outro');
    put.flush({ error: 'x' }, { status: 400, statusText: 'Bad Request' });

    expect(page.configs().find((c) => c.role === 'Dev')!.adapterName).toBe('claude-code');
  });

  it('escolher uma máquina consulta, testa e conecta o agente DAQUELA máquina, sem mexer no aviso global do servidor', () => {
    const machine: ExecutionEnvironment = {
      id: 'e1', name: 'notebook-node22', url: 'http://192.168.0.20:8787', workspaceRoot: '/home/eu/af', tags: '', hasToken: true, projectCount: 0, createdAt: '', maxConcurrency: 1,
    };
    open([agent({ authenticated: true, lastTest: { ok: true, message: 'OK', durationMs: 1, costUsd: 0, at: '' } })], [machine]);
    const shared = TestBed.inject(AgentsService);
    expect(shared.agents()?.[0].authenticated).toBeTrue();

    fixture.componentInstance.select('e1');
    http.expectOne('/api/agents/environment?environmentId=e1').flush({ mode: 'host', description: 'no computador, pelo host agent (192.168.0.20:8787)' });
    http.expectOne('/api/agents?environmentId=e1').flush([agent({ authenticated: false })]);
    fixture.detectChanges();

    expect(text()).toContain('Não conectado');
    expect(shared.agents()?.[0].authenticated).toBeTrue();   // o aviso da barra continua sendo o do servidor

    click('Conectar');
    http.expectOne({ method: 'POST', url: '/api/agents/claude-code/login?environmentId=e1' })
      .flush({ sessionId: 's1', url: 'https://claude.ai/login', state: 'WaitingForCode', message: null });
    fixture.detectChanges();

    fixture.componentInstance.setCode('claude-code', 'abc123');
    fixture.detectChanges();
    click('Confirmar');
    http.expectOne({ method: 'POST', url: '/api/agents/claude-code/login/s1/code?environmentId=e1' })
      .flush({ sessionId: 's1', url: null, state: 'Succeeded', message: 'Conectado.' });
    http.expectOne('/api/agents/claude-code?refresh=true&environmentId=e1')
      .flush(agent({ authenticated: true }));
    http.expectOne({ method: 'POST', url: '/api/agents/claude-code/test?environmentId=e1' })
      .flush({ ok: true, message: 'OK', durationMs: 1, costUsd: 0, at: '' });
    fixture.detectChanges();

    expect(text()).toContain('Conectado');
    expect(shared.agents()?.[0].authenticated).toBeTrue();
  });

  it('máquina offline: mostra o motivo real, não "reconstrua a imagem"', () => {
    const machine: ExecutionEnvironment = {
      id: 'e1', name: 'notebook', url: 'http://192.168.0.20:8787', workspaceRoot: '/x', tags: '', hasToken: true, projectCount: 0, createdAt: '', maxConcurrency: 1,
    };
    open([agent({ authenticated: true, lastTest: { ok: true, message: 'OK', durationMs: 1, costUsd: 0, at: '' } })], [machine]);

    fixture.componentInstance.select('e1');
    http.expectOne('/api/agents/environment?environmentId=e1').flush({ mode: 'host', description: 'host agent' });
    http.expectOne('/api/agents?environmentId=e1')
      .flush([agent({ installed: false, version: null, authenticated: false, detail: 'Host agent inacessível em http://192.168.0.20:8787/' })]);
    fixture.detectChanges();

    expect(text()).toContain('Host agent inacessível');
    expect(text()).toContain('tela Ambientes');
    expect(text()).not.toContain('Reconstrua a imagem');
  });
});
