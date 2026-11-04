import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { GitSshStatus } from '../core/models';
import { GitPage } from './git.page';

const status = (over: Partial<GitSshStatus> = {}): GitSshStatus => ({
  supported: true, reason: null, hasKey: false, publicKey: null, fingerprint: null, createdAt: null, directory: '/home/app/.ssh', ...over,
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

  function open(initial: GitSshStatus): void {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(GitPage);
    fixture.detectChanges();
    http.expectOne('/api/git/ssh').flush(initial);
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

    expect(byTestId('unsupported')?.textContent).toContain('git do seu computador');
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
});
