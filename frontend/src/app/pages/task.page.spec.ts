import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { DemandChanged, Project, RunEvent, WorkItem, WorkItemChanged } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { TaskPage } from './task.page';

const task = (over: Partial<WorkItem> = {}): WorkItem => ({
  id: 'w1', demandId: 'd1', title: 'Criar API', description: 'd', acceptanceCriteria: 'c', estimate: 1, state: 'Done',
  reworkCount: 0, branchName: 'agentforge/task-w1', blockedReason: null, blockedFromState: null, dependsOnIds: [], createdAt: '2026-01-01T00:00:00Z', ...over,
});
const project = (over: Partial<Project> = {}): Project => ({
  id: 'p1', name: 'Loja', repositoryUrl: 'https://github.com/o/r.git', baseBranch: 'main', buildCommand: '', testCommand: '', autoMode: false,
  createdAt: '', hasGitHubToken: true, pullRequestBaseBranch: 'develop', ...over,
} as Project);

describe('TaskPage (pull request)', () => {
  let fixture: ComponentFixture<TaskPage>;
  let http: HttpTestingController;

  const root = () => fixture.nativeElement as HTMLElement;
  const pr = () => root().querySelector('[data-testid="pr"]');

  function open(w: WorkItem, p: Project): void {
    TestBed.configureTestingModule({
      imports: [TaskPage],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        {
          provide: RealtimeService,
          useValue: {
            bindProject: () => undefined, runEvent$: new Subject<RunEvent>(), workItemChanged$: new Subject<WorkItemChanged>(),
            demandChanged$: new Subject<DemandChanged>(), reconnected$: new Subject<void>(),
          },
        },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(TaskPage);
    fixture.componentRef.setInput('id', 'w1');
    fixture.detectChanges();
    http.expectOne('/api/work-items/w1').flush(w);
    http.expectOne('/api/demands/d1').flush({ id: 'd1', projectId: 'p1', title: 'D', description: '', status: 'InProgress', createdAt: '' });
    http.expectOne('/api/projects/p1').flush(p);
    http.expectOne((r) => r.url === '/api/agent-runs').flush([]);
    http.expectOne((r) => r.url === '/api/review-findings').flush([]);
    http.expectOne('/api/work-items/w1/diff').flush('');
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('tarefa concluída em projeto com token: oferece abrir o PR para a branch de destino do projeto', () => {
    open(task(), project());

    expect(pr()).not.toBeNull();
    expect((root().querySelector('[data-testid="pr-base"]') as HTMLInputElement).placeholder).toBe('develop');
    expect(pr()!.textContent).toContain('Abrir PR');
    expect(pr()!.textContent).toContain('agentforge/task-w1');
  });

  it('abre o PR (com a branch digitada), avisa e mostra o link depois', () => {
    open(task(), project());
    const input = root().querySelector('[data-testid="pr-base"]') as HTMLInputElement;
    input.value = 'release/1.0';
    input.dispatchEvent(new Event('input'));

    fixture.componentInstance.openPullRequest();
    const req = http.expectOne({ method: 'POST', url: '/api/work-items/w1/pull-request' });
    expect(req.request.body).toEqual({ baseBranch: 'release/1.0' });
    req.flush({ url: 'https://github.com/o/r/pull/7', number: 7, branch: 'agentforge/task-w1', baseBranch: 'release/1.0', alreadyExisted: false });
    http.expectOne('/api/work-items/w1').flush(task({ pullRequestUrl: 'https://github.com/o/r/pull/7', pullRequestNumber: 7 }));
    fixture.detectChanges();

    const link = root().querySelector('[data-testid="pr-link"]') as HTMLAnchorElement;
    expect(link.href).toBe('https://github.com/o/r/pull/7');
    expect(link.textContent).toContain('#7');
    expect(pr()!.textContent).not.toContain('Abrir PR');
  });

  it('campo vazio não envia branch (vale a do projeto) e o motivo do erro aparece', () => {
    open(task(), project());
    const snack = spyOn(fixture.componentInstance['snack'], 'open');

    fixture.componentInstance.openPullRequest();
    const req = http.expectOne({ method: 'POST', url: '/api/work-items/w1/pull-request' });
    expect(req.request.body).toEqual({ baseBranch: null });
    req.flush({ error: 'O GitHub recusou o token no push.' }, { status: 502, statusText: 'Bad Gateway' });

    expect(snack).toHaveBeenCalledWith('O GitHub recusou o token no push.', 'OK', { duration: 9000 });
    expect(fixture.componentInstance.openingPr()).toBeFalse();
  });

  it('sem token no projeto, explica onde cadastrar em vez de oferecer o botão', () => {
    open(task(), project({ hasGitHubToken: false }));

    expect(root().querySelector('[data-testid="pr-hint"]')!.textContent).toContain('token do GitHub');
    expect(pr()!.textContent).not.toContain('Abrir PR');
  });

  it('só aparece para tarefas concluídas', () => {
    open(task({ state: 'AwaitingApproval' }), project());

    expect(pr()).toBeNull();
  });
});
