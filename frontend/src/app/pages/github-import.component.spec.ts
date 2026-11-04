import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { GitHubImportResult, GitHubRepo } from '../core/models';
import { GithubImportComponent } from './github-import.component';

const repo = (fullName: string, priv: boolean): GitHubRepo => ({
  fullName, name: fullName.split('/')[1], private: priv, defaultBranch: 'main', description: null,
  cloneUrl: `https://github.com/${fullName}.git`, sshUrl: `git@github.com:${fullName}.git`, updatedAt: null, canAdmin: true,
});

describe('GithubImportComponent', () => {
  let fixture: ComponentFixture<GithubImportComponent>;
  let http: HttpTestingController;
  let component: GithubImportComponent;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const byTestId = (id: string) => (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(GithubImportComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('lista os repositórios do token, filtra e preenche o campo ao escolher um', () => {
    component.token = ' ghp_abcdefghijklmnopqrstuvwxyz ';
    component.list();
    const req = http.expectOne({ method: 'POST', url: '/api/github/repos' });
    expect(req.request.body).toEqual({ token: 'ghp_abcdefghijklmnopqrstuvwxyz' });
    req.flush([repo('octo/publico', false), repo('octo/privado', true)]);
    fixture.detectChanges();

    expect(text()).toContain('octo/publico');
    expect(text()).toContain('privado');

    component.filter.set('priv');
    fixture.detectChanges();
    expect(text()).not.toContain('octo/publico');

    component.select(component.filtered()[0]);
    expect(component.repository).toBe('octo/privado');
  });

  it('mostra a mensagem da API quando o GitHub recusa o token', () => {
    component.token = 'ghp_abcdefghijklmnopqrstuvwxyz';
    component.list();
    http.expectOne('/api/github/repos').flush({ error: 'O GitHub recusou o token.' }, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(byTestId('error')?.textContent).toContain('O GitHub recusou o token.');
  });

  it('importa enviando as opções, limpa o token e avisa quem abriu', () => {
    const imported: GitHubImportResult[] = [];
    component.imported.subscribe((r) => imported.push(r));
    component.token = 'ghp_abcdefghijklmnopqrstuvwxyz';
    component.repository = ' octo/privado ';
    component.testCommand = 'dotnet test';
    component.doImport();

    const req = http.expectOne({ method: 'POST', url: '/api/github/import' });
    expect(req.request.body).toEqual({
      repository: 'octo/privado', token: 'ghp_abcdefghijklmnopqrstuvwxyz', addDeployKey: true, buildCommand: '', testCommand: 'dotnet test', autoMode: false,
    });
    const result: GitHubImportResult = {
      project: { id: 'p1', name: 'privado', repositoryUrl: 'git@github.com:octo/privado.git', baseBranch: 'main', buildCommand: '', testCommand: 'dotnet test', autoMode: false, createdAt: '' },
      cloned: true, deployKeyAdded: true, notes: ['Chave registrada como deploy key somente leitura.'], repository: repo('octo/privado', true),
    };
    req.flush(result);
    fixture.detectChanges();

    expect(imported).toEqual([result]);
    expect(component.token).toBe('');
    expect(text()).toContain('importado e clonado');
    expect(text()).toContain('deploy key somente leitura');
  });

  it('repositório público não precisa de token: ele nem é enviado', () => {
    component.repository = 'octocat/Hello-World';
    component.doImport();

    const req = http.expectOne('/api/github/import');
    expect('token' in req.request.body).toBeFalse();
    req.flush({ error: 'O projeto não foi criado: não foi possível clonar. falhou' }, { status: 502, statusText: 'Bad Gateway' });
    fixture.detectChanges();

    expect(byTestId('error')?.textContent).toContain('não foi possível clonar');
    expect(component.importing()).toBeFalse();
  });
});
