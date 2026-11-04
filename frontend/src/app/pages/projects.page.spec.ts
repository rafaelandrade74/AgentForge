import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { ExecutionEnvironment, Project, StackProfile } from '../core/models';
import { ProjectsPage } from './projects.page';

const nodeProfile: StackProfile = {
  id: 's1', name: 'Node 22', description: '', image: '', dockerfile: 'FROM node:22', network: 'bridge', cpuLimit: 2, memoryMb: 4096,
  builtIn: true, projectCount: 0, createdAt: '',
};

const machine: ExecutionEnvironment = {
  id: 'e1', name: 'notebook-node22', url: 'http://192.168.0.20:8787', workspaceRoot: '/home/eu/af', tags: '', hasToken: true, projectCount: 1, createdAt: '', maxConcurrency: 1,
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
    http.expectOne('/api/stack-profiles').flush([nodeProfile]);
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

  it('o perfil de stack só aparece com um ambiente escolhido e vai no salvar; sem ambiente o perfil é descartado', () => {
    open(project());
    const page = fixture.componentInstance;
    const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';

    page.toggleForm();
    fixture.detectChanges();
    expect(text()).not.toContain('Perfil de stack');

    page.draft.name = 'Loja';
    page.draft.repositoryUrl = 'git@github.com:org/loja.git';
    page.draft.environmentId = 'e1';
    page.draft.stackProfileId = 's1';
    fixture.detectChanges();
    expect(text()).toContain('Perfil de stack');

    page.save();
    const saved = http.expectOne({ method: 'POST', url: '/api/projects' });
    expect(saved.request.body.stackProfileId).toBe('s1');
    saved.flush(project());
    http.expectOne('/api/projects').flush([project()]);

    page.toggleForm();
    page.draft.name = 'Outra';
    page.draft.repositoryUrl = 'x';
    page.draft.environmentId = null;
    page.draft.stackProfileId = 's1';
    page.save();
    const second = http.expectOne({ method: 'POST', url: '/api/projects' });
    expect(second.request.body.stackProfileId).toBeNull();
    second.flush(project());
    http.expectOne('/api/projects').flush([project()]);
  });

  it('mostra o perfil do projeto no cartão', () => {
    open(project({ environmentId: 'e1', stackProfileId: 's1' }));

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('perfil: Node 22');
  });

  it('Tech Lead e Security podem rodar em outra máquina; Dev e QA não aparecem para escolha', () => {
    open(project());
    const page = fixture.componentInstance;
    const root = fixture.nativeElement as HTMLElement;

    page.toggleForm();
    fixture.detectChanges();
    const block = root.querySelector('[data-testid="role-envs"]')!;
    expect(block.textContent).toContain('Tech Lead roda em');
    expect(block.textContent).toContain('Security roda em');
    expect(block.textContent).not.toContain('Dev roda em');
    expect(block.textContent).not.toContain('QA roda em');

    page.draft.name = 'Loja';
    page.draft.repositoryUrl = 'x';
    page.draft.environmentId = 'e1';
    page.setRoleEnv('TechLead', 'e1');
    page.setRoleEnv('Security', null);
    page.save();

    const req = http.expectOne({ method: 'POST', url: '/api/projects' });
    expect(req.request.body.roleEnvironments).toEqual({ TechLead: 'e1', Security: null });
    req.flush(project());
    http.expectOne('/api/projects').flush([project()]);
  });

  it('ao editar, os papéis já movidos aparecem selecionados', () => {
    open(project({ environmentId: 'e1', roleEnvironments: { Security: 'e1' } }));
    const page = fixture.componentInstance;

    page.edit(project({ environmentId: 'e1', roleEnvironments: { Security: 'e1' } }));

    expect(page.roleEnv('Security')).toBe('e1');
    expect(page.roleEnv('TechLead')).toBeNull();
  });
});
