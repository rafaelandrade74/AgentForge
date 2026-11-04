import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { EnvironmentList, EnvironmentLoadReport, EnvironmentTest, ExecutionEnvironment } from '../core/models';
import { EnvironmentsPage } from './environments.page';

const env = (over: Partial<ExecutionEnvironment> = {}): ExecutionEnvironment => ({
  id: 'e1', name: 'notebook-node22', url: 'http://192.168.0.20:8787', workspaceRoot: 'C:/agentforge', tags: 'node22, dotnet8',
  hasToken: true, projectCount: 2, createdAt: '2026-01-01T00:00:00Z', maxConcurrency: 2, ...over,
});
const list = (items: ExecutionEnvironment[] = [env()]): EnvironmentList => ({
  default: { mode: 'local', description: 'No container da API' }, items,
});

const loadReport = (over: Partial<EnvironmentLoadReport> = {}): EnvironmentLoadReport => ({
  environments: [
    { id: null, name: 'Padrão do servidor', kind: 'default', maxConcurrency: 1, runningTasks: 0, queuedTasks: 0, runningAgents: 0, projectCount: 0 },
    { id: 'e1', name: 'notebook-node22', kind: 'host', maxConcurrency: 2, runningTasks: 2, queuedTasks: 3, runningAgents: 1, projectCount: 2 },
  ],
  waiting: [],
  ...over,
});

describe('EnvironmentsPage (ambientes de execução)', () => {
  let fixture: ComponentFixture<EnvironmentsPage>;
  let http: HttpTestingController;

  const root = () => fixture.nativeElement as HTMLElement;
  const text = () => root().textContent ?? '';
  const click = (scope: ParentNode, label: string) => {
    const b = Array.from(scope.querySelectorAll('button')).find((x) => x.textContent?.trim().startsWith(label));
    expect(b).withContext(`botão "${label}"`).toBeDefined();
    b!.click();
    fixture.detectChanges();
  };
  const card = (testId: string) => root().querySelector(`[data-testid="${testId}"]`) as HTMLElement;

  function open(initial: EnvironmentList = list(), report: EnvironmentLoadReport = loadReport()): void {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(EnvironmentsPage);
    fixture.detectChanges();
    http.expectOne('/api/environments').flush(initial);
    http.expectOne('/api/environments/load').flush(report);
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('mostra o padrão do servidor e as máquinas cadastradas, sem nunca exibir um token', () => {
    open();

    expect(card('default-env').textContent).toContain('No container da API');
    expect(card('env').textContent).toContain('notebook-node22');
    expect(card('env').textContent).toContain('node22');
    expect(card('env').textContent).toContain('C:/agentforge');
    expect(card('env').textContent).toContain('2 projeto(s)');
    expect(root().querySelector('input[type="password"]')).toBeNull();
  });

  it('sem máquinas, explica como cadastrar', () => {
    open(list([]));
    expect(text()).toContain('Nenhuma máquina cadastrada');
  });

  it('cadastra uma máquina enviando o token só na requisição e recarrega a lista', () => {
    open(list([]));
    click(root(), 'Novo ambiente');

    const page = fixture.componentInstance;
    page.draft = { name: 'servidor-py', url: 'http://10.0.0.5:8787', token: 'segredo-do-host-agent-1234567890', workspaceRoot: '/srv/af', tags: 'python3.12' };
    page.save();

    const req = http.expectOne({ method: 'POST', url: '/api/environments' });
    expect(req.request.body.token).toBe('segredo-do-host-agent-1234567890');
    req.flush(env({ id: 'e2', name: 'servidor-py' }));
    http.expectOne('/api/environments').flush(list([env({ id: 'e2', name: 'servidor-py' })]));
    fixture.detectChanges();

    expect(text()).toContain('servidor-py');
    expect(text()).not.toContain('segredo-do-host-agent');
    expect(root().querySelector('form')).toBeNull();
  });

  it('ao editar, o token vem vazio (vazio mantém o atual) e o PUT vai para o id certo', () => {
    open();
    click(card('env'), 'Editar');

    const page = fixture.componentInstance;
    expect(page.draft.token).toBe('');
    expect(page.draft.name).toBe('notebook-node22');
    expect(text()).toContain('vazio mantém o atual');

    page.draft = { ...page.draft, tags: 'node22' };
    page.save();
    const req = http.expectOne({ method: 'PUT', url: '/api/environments/e1' });
    expect(req.request.body.token).toBe('');
    req.flush(env({ tags: 'node22' }));
    http.expectOne('/api/environments').flush(list([env({ tags: 'node22' })]));
  });

  it('testa a conexão e mostra as ferramentas da máquina', () => {
    open();
    click(card('env'), 'Testar');

    const result: EnvironmentTest = {
      reachable: true, error: null, workspaceRootExists: false,
      tools: [
        { tool: 'git', ok: true, detail: 'git version 2.43.0' },
        { tool: 'dotnet', ok: false, detail: 'não disponível (ausente ou fora da lista permitida do host agent)' },
      ],
    };
    http.expectOne({ method: 'POST', url: '/api/environments/e1/test' }).flush(result);
    fixture.detectChanges();

    const shown = card('env').textContent ?? '';
    expect(shown).toContain('conectado');
    expect(shown).toContain('git version 2.43.0');
    expect(shown).toContain('não disponível');
    expect(shown).toContain('a pasta dos clones será criada');
  });

  it('máquina sem resposta aparece como sem conexão, com o motivo', () => {
    open();
    click(card('env'), 'Testar');
    http.expectOne('/api/environments/e1/test').flush({ reachable: false, error: 'Host agent inacessível em http://192.168.0.20:8787', workspaceRootExists: false, tools: [] });
    fixture.detectChanges();

    expect(card('env').textContent).toContain('sem conexão');
    expect(card('env').textContent).toContain('Host agent inacessível');
  });

  it('o teste do padrão do servidor usa a rota default e não fala em pasta de clones', () => {
    open();
    click(card('default-env'), 'Testar');
    http.expectOne({ method: 'POST', url: '/api/environments/default/test' })
      .flush({ reachable: true, error: null, workspaceRootExists: false, tools: [{ tool: 'git', ok: true, detail: 'git version 2.43.0' }] });
    fixture.detectChanges();

    expect(card('default-env').textContent).toContain('git version 2.43.0');
    expect(card('default-env').textContent).not.toContain('será criada');
  });

  it('ambiente em uso não pode ser excluído: mostra o motivo devolvido pela API', () => {
    open();
    const page = fixture.componentInstance;
    const messages: string[] = [];
    (page as unknown as { snack: { open: (m: string) => void } }).snack = { open: (m: string) => void messages.push(m) };

    // a confirmação do diálogo é testada à parte; aqui só o tratamento do erro da API
    page['api'].deleteEnvironment('e1').subscribe({
      error: (err) => (page as unknown as { snack: { open: (m: string) => void } }).snack.open(page['message'](err, 'falhou')),
    });
    http.expectOne({ method: 'DELETE', url: '/api/environments/e1' })
      .flush({ error: 'O ambiente está em uso por 2 projeto(s).' }, { status: 409, statusText: 'Conflict' });

    expect(messages).toEqual(['O ambiente está em uso por 2 projeto(s).']);
  });

  it('o painel de carga mostra tarefas rodando, na fila e as que esperam uma máquina fora do ar', () => {
    open(list(), loadReport({
      waiting: [{ kind: 'task', id: 't1', title: 'Criar API', project: 'Loja', reason: 'Host agent inacessível em http://192.168.0.20:8787/', retryAt: new Date(Date.now() + 30000).toISOString(), attempts: 2 }],
    }));

    const rows = Array.from(root().querySelectorAll('[data-testid="load-row"]')).map((r) => r.textContent ?? '');
    expect(rows.length).toBe(2);
    expect(rows[1]).toContain('notebook-node22');
    expect(rows[1]).toContain('2 / 2');
    expect(rows[1]).toContain('3 esperando vaga');
    const waiting = root().querySelector('[data-testid="waiting-row"]')!.textContent!;
    expect(waiting).toContain('Criar API');
    expect(waiting).toContain('Host agent inacessível');
    expect(waiting).toContain('tentativa 2');
  });

  it('o botão Atualizar recarrega a carga', () => {
    open();

    const b = Array.from(root().querySelectorAll('button')).find((x) => x.textContent?.trim() === 'Atualizar')!;
    b.click();

    http.expectOne('/api/environments/load').flush(loadReport({ environments: [] }));
    fixture.detectChanges();
    expect(root().querySelectorAll('[data-testid="load-row"]').length).toBe(0);
  });

  it('cadastra a máquina com o limite de tarefas simultâneas', () => {
    open();

    fixture.componentInstance.draft = { name: 'novo', url: 'http://192.168.0.30:8787', token: 'segredo-do-host-agent-1234567890', workspaceRoot: '/af', tags: '', maxConcurrency: 4 };
    fixture.componentInstance.save();

    const req = http.expectOne({ method: 'POST', url: '/api/environments' });
    expect(req.request.body.maxConcurrency).toBe(4);
    req.flush(env({ id: 'e9', name: 'novo', maxConcurrency: 4 }));
    http.expectOne('/api/environments').flush(list([env({ id: 'e9', name: 'novo', maxConcurrency: 4 })]));
  });
});
