import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { ExecutionEnvironment, GitSshStatus } from '../core/models';
import { GitPage } from './git.page';

const status = (over: Partial<GitSshStatus> = {}): GitSshStatus => ({
  supported: true, reason: null, hasKey: false, publicKey: null, fingerprint: null, createdAt: null, directory: '/home/app/.ssh', ...over,
});

const machine = (over: Partial<ExecutionEnvironment> = {}): ExecutionEnvironment => ({
  id: 'e1', name: 'notebook-node22', url: 'http://192.168.0.20:8787', workspaceRoot: '/home/eu/af', tags: '', hasToken: true, projectCount: 0, createdAt: '', maxConcurrency: 1, ...over,
});

describe('GitPage (credenciais SSH)', () => {
  let fixture: ComponentFixture<GitPage>;
  let http: HttpTestingController;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const byTestId = (id: string) => (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const click = (label: string) => {
    const b = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')).find((x) => x.textContent?.trim().startsWith(label));
    expect(b).withContext(`botão "${label}"`).toBeDefined();
    b!.click();
    fixture.detectChanges();
  };

  function open(initial: GitSshStatus, environments: ExecutionEnvironment[] = []): void {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(GitPage);
    fixture.detectChanges();
    http.expectOne('/api/git/ssh').flush(initial);
    http.expectOne('/api/environments').flush({ default: { mode: 'local', description: 'container' }, items: environments });
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('sem chave: oferece gerar e, depois de gerada, mostra só a pública com as instruções do GitHub', () => {
    open(status());
    expect(text()).toContain('Ainda não há chave');

    click('Gerar chave SSH');
    const req = http.expectOne({ method: 'POST', url: '/api/git/ssh/generate' });
    expect(req.request.body).toEqual({ overwrite: false });
    req.flush(status({ hasKey: true, publicKey: 'ssh-ed25519 AAAAC3Nz agentforge@host', fingerprint: 'SHA256:abc', createdAt: '2026-01-01T10:00:00Z' }));
    fixture.detectChanges();

    expect(byTestId('public-key')?.textContent).toContain('ssh-ed25519 AAAAC3Nz agentforge@host');
    expect(text()).toContain('SHA256:abc');
    expect(text()).toContain('Deploy keys');
    expect(text()).toContain('Allow write access');
    expect(text()).not.toContain('PRIVATE KEY');
  });

  it('testa o acesso a um repositório e mostra o resultado', () => {
    open(status({ hasKey: true, publicKey: 'ssh-ed25519 AAAA x' }));

    const page = fixture.componentInstance;
    page.repoUrl = '  git@github.com:org/repo.git  ';
    page.test();
    const req = http.expectOne({ method: 'POST', url: '/api/git/ssh/test' });
    expect(req.request.body).toEqual({ repositoryUrl: 'git@github.com:org/repo.git' });
    req.flush({ ok: false, message: 'A chave não tem acesso a este repositório.' });
    fixture.detectChanges();

    expect(byTestId('access-result')?.textContent).toContain('Sem acesso');
    expect(byTestId('access-result')?.textContent).toContain('A chave não tem acesso');

    page.test();
    http.expectOne('/api/git/ssh/test').flush({ ok: true, message: 'Acesso confirmado (3 branches visíveis).' });
    fixture.detectChanges();
    expect(byTestId('access-result')?.textContent).toContain('Acesso ok');
  });

  it('no modo host agent explica que o git é o do computador e não oferece gerar chave', () => {
    open(status({ supported: false, reason: 'O git roda no seu computador (host agent): ele usa as credenciais que você já configurou lá.' }));

    expect(byTestId('unsupported')?.textContent).toContain('credenciais da própria máquina');
    expect(text()).toContain('credenciais que você já configurou');
    expect(text()).not.toContain('Gerar chave SSH');
  });

  it('mostra o motivo quando a API recusa gerar (ex.: já existe chave)', () => {
    open(status());
    const snack = spyOn(fixture.componentInstance['snack'], 'open');

    fixture.componentInstance.generate(false);
    http.expectOne('/api/git/ssh/generate').flush({ error: 'Já existe uma chave.' }, { status: 409, statusText: 'Conflict' });

    expect(snack).toHaveBeenCalledWith('Já existe uma chave.', 'OK', { duration: 6000 });
  });

  it('sem ambientes cadastrados não mostra o seletor; com ambientes, escolhe a máquina e usa a chave dela', () => {
    open(status());
    expect(byTestId('env-select')).toBeNull();
    TestBed.resetTestingModule();

    open(status(), [machine()]);
    expect(byTestId('env-select')).not.toBeNull();

    fixture.componentInstance.select('e1');
    const reload = http.expectOne('/api/git/ssh?environmentId=e1');
    reload.flush(status({ hasKey: true, publicKey: 'ssh-ed25519 BBBB agentforge@notebook', directory: '/home/eu/.agentforge/ssh' }));
    fixture.detectChanges();

    expect(byTestId('public-key')?.textContent).toContain('agentforge@notebook');
    expect(text()).toContain('Chave SSH deste ambiente');

    // gerar, testar e remover passam sempre pelo mesmo ambiente
    fixture.componentInstance.generate(true);
    expect(http.expectOne({ method: 'POST', url: '/api/git/ssh/generate?environmentId=e1' }).request.body).toEqual({ overwrite: true });
    fixture.componentInstance.repoUrl = 'git@github.com:org/repo.git';
    fixture.componentInstance.test();
    http.expectOne({ method: 'POST', url: '/api/git/ssh/test?environmentId=e1' });
  });

  it('volta ao padrão do servidor sem enviar environmentId', () => {
    open(status(), [machine()]);
    fixture.componentInstance.select('e1');
    http.expectOne('/api/git/ssh?environmentId=e1').flush(status());

    fixture.componentInstance.select(null);

    http.expectOne('/api/git/ssh').flush(status());
    expect(fixture.componentInstance.environmentId()).toBeNull();
  });

  it('máquina fora do ar: mostra o motivo em vez de quebrar a tela', () => {
    open(status(), [machine()]);

    fixture.componentInstance.select('e1');
    http.expectOne('/api/git/ssh?environmentId=e1')
      .flush({ error: 'O ambiente deste projeto não existe mais.' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(byTestId('unsupported')?.textContent).toContain('não existe mais');
  });

  it('abre já no ambiente do link ?environmentId= (tela Ambientes)', () => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), provideRouter([])],
    });
    TestBed.overrideProvider(ActivatedRoute, { useValue: { snapshot: { queryParamMap: convertToParamMap({ environmentId: 'e9' }) } } });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(GitPage);
    fixture.detectChanges();

    http.expectOne('/api/git/ssh?environmentId=e9').flush(status());
    http.expectOne('/api/environments').flush({ default: { mode: 'local', description: '' }, items: [machine({ id: 'e9' })] });
    expect(fixture.componentInstance.environmentId()).toBe('e9');
  });
});
