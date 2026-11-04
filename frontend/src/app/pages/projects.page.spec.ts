import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { ExecutionEnvironment, Project } from '../core/models';
import { ProjectsPage } from './projects.page';

const machine: ExecutionEnvironment = {
  id: 'e1', name: 'notebook-node22', url: 'http://192.168.0.20:8787', workspaceRoot: '/home/eu/af', tags: '', hasToken: true, projectCount: 1, createdAt: '',
};
const project = (over: Partial<Project> = {}): Project => ({
  id: 'p1', name: 'Loja', repositoryUrl: 'git@github.com:org/loja.git', baseBranch: 'main', buildCommand: '', testCommand: '',
  autoMode: false, environmentId: null, createdAt: '', ...over,
} as Project);

describe('ProjectsPage (troca de ambiente)', () => {
  let fixture: ComponentFixture<ProjectsPage>;
  let http: HttpTestingController;

  function open(p: Project): void {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), provideRouter([])] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ProjectsPage);
    fixture.detectChanges();
    http.expectOne('/api/projects').flush([p]);
    http.expectOne('/api/environments').flush({ default: { mode: 'local', description: '' }, items: [machine] });
    fixture.detectChanges();
  }

  const hint = () => (fixture.nativeElement as HTMLElement).querySelector('[data-testid="env-change-hint"]');

  afterEach(() => http.verify());

  it('avisa que o clone não é movido só quando se muda o ambiente de um projeto existente', () => {
    open(project());
    const page = fixture.componentInstance;

    page.edit(project());
    fixture.detectChanges();
    expect(hint()).toBeNull();                       // abriu a edição: nada mudou

    page.draft.environmentId = 'e1';
    fixture.detectChanges();
    expect(hint()?.textContent).toContain('próximo uso clona o repositório na nova máquina');
    expect(hint()?.textContent).toContain('tela Git');

    page.draft.environmentId = null;                  // voltou ao original: some
    fixture.detectChanges();
    expect(hint()).toBeNull();
  });

  it('projeto novo não mostra o aviso ao escolher um ambiente', () => {
    open(project());
    const page = fixture.componentInstance;

    page.toggleForm();
    page.draft.environmentId = 'e1';
    fixture.detectChanges();

    expect(hint()).toBeNull();
  });
});
