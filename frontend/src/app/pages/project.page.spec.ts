import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { Subject } from 'rxjs';
import { DemandChanged, RunEvent, WorkItem, WorkItemChanged } from '../core/models';
import { RealtimeService } from '../core/realtime.service';
import { ProjectPage } from './project.page';

const item = (over: Partial<WorkItem>): WorkItem => ({
  id: 'w1', demandId: 'd1', title: 'Tarefa 1', description: '', acceptanceCriteria: '', estimate: 0, state: 'Todo',
  reworkCount: 0, branchName: null, blockedReason: null, dependsOnIds: [], createdAt: '2026-01-01T00:00:00Z', ...over,
});

describe('ProjectPage (quadro)', () => {
  let fixture: ComponentFixture<ProjectPage>;
  let http: HttpTestingController;
  const workItemChanged$ = new Subject<WorkItemChanged>();

  beforeEach(() => {
    const fakeRealtime = {
      bindProject: () => undefined,
      runEvent$: new Subject<RunEvent>(),
      workItemChanged$,
      demandChanged$: new Subject<DemandChanged>(),
      reconnected$: new Subject<void>(),
    };
    TestBed.configureTestingModule({
      imports: [ProjectPage],
      providers: [
        provideRouter([]), provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: RealtimeService, useValue: fakeRealtime },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ProjectPage);
    fixture.componentRef.setInput('id', 'p1');
    fixture.detectChanges();

    http.expectOne('/api/projects/p1').flush({
      id: 'p1', name: 'Demo', repositoryUrl: 'u', baseBranch: 'main', buildCommand: '', testCommand: '', autoMode: false, createdAt: '',
    });
    http.expectOne((r) => r.url === '/api/demands').flush([
      { id: 'd1', projectId: 'p1', title: 'Filtro por status', description: '', status: 'InProgress', createdAt: '' },
    ]);
    http.expectOne((r) => r.url === '/api/work-items').flush([
      item({ id: 'w1', title: 'Criar filtro', state: 'Todo' }),
      item({ id: 'w2', title: 'Escrever testes', state: 'Blocked', reworkCount: 3 }),
    ]);
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  const columnOf = (title: string): string => {
    const cards = Array.from(fixture.nativeElement.querySelectorAll('section.col') as NodeListOf<HTMLElement>);
    return cards.find((c) => c.textContent?.includes(title))?.getAttribute('aria-label') ?? '';
  };

  it('mostra cada tarefa na coluna do seu estado', () => {
    expect(columnOf('Criar filtro')).toBe('A fazer');
    expect(columnOf('Escrever testes')).toBe('Bloqueada');
    expect(fixture.nativeElement.textContent).toContain('retrabalho 3');
    expect(fixture.nativeElement.textContent).toContain('Filtro por status');
  });

  it('move o cartão quando chega uma mudança de estado pelo tempo real', () => {
    workItemChanged$.next({
      projectId: 'p1', demandId: 'd1', workItemId: 'w1', state: 'InDevelopment', reworkCount: 0,
      blockedReason: null, branchName: 'agentforge/task-w1', at: '',
    });
    fixture.detectChanges();

    expect(columnOf('Criar filtro')).toBe('Desenvolvimento');
  });

  it('ignora eventos de outros projetos e recarrega quando a tarefa é desconhecida', () => {
    workItemChanged$.next({
      projectId: 'outro', demandId: 'd9', workItemId: 'w1', state: 'Done', reworkCount: 0, blockedReason: null, branchName: null, at: '',
    });
    fixture.detectChanges();
    expect(columnOf('Criar filtro')).toBe('A fazer');

    workItemChanged$.next({
      projectId: 'p1', demandId: 'd1', workItemId: 'nova', state: 'Backlog', reworkCount: 0, blockedReason: null, branchName: null, at: '',
    });
    http.expectOne((r) => r.url === '/api/work-items').flush([item({ id: 'nova', title: 'Tarefa nova', state: 'Backlog' })]);
    fixture.detectChanges();
    expect(columnOf('Tarefa nova')).toBe('Backlog');
  });
});
