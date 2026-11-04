import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ExecutionEnvironment, StackProfile } from '../core/models';
import { ProfilesPage } from './profiles.page';

const profile = (over: Partial<StackProfile> = {}): StackProfile => ({
  id: 's1', name: 'Node 22', description: 'Node.js 22.', image: '', dockerfile: 'FROM node:22\n', network: 'bridge', cpuLimit: 2, memoryMb: 4096,
  builtIn: true, projectCount: 1, createdAt: '', ...over,
});
const machine: ExecutionEnvironment = {
  id: 'e1', name: 'linux-node', url: 'http://192.168.0.20:8787', workspaceRoot: '/home/eu/af', tags: '', hasToken: true, projectCount: 0, createdAt: '', maxConcurrency: 1,
};

describe('ProfilesPage', () => {
  let fixture: ComponentFixture<ProfilesPage>;
  let http: HttpTestingController;

  const root = () => fixture.nativeElement as HTMLElement;
  const click = (label: string) => {
    const b = Array.from(root().querySelectorAll('button')).find((x) => x.textContent?.trim().startsWith(label));
    expect(b).withContext(`botão "${label}"`).toBeDefined();
    b!.click();
    fixture.detectChanges();
  };

  function open(profiles: StackProfile[], environments: ExecutionEnvironment[] = [machine]): void {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ProfilesPage);
    fixture.detectChanges();
    http.expectOne('/api/stack-profiles').flush(profiles);
    http.expectOne('/api/environments').flush({ default: { mode: 'local', description: '' }, items: environments });
    fixture.detectChanges();
  }

  afterEach(() => http.verify());

  it('lista os perfis; os prontos não têm editar nem excluir', () => {
    open([profile(), profile({ id: 's2', name: 'Meu Go', builtIn: false, network: 'none' })]);

    const cards = root().querySelectorAll('[data-testid="profile"]');
    expect(cards.length).toBe(2);
    expect(cards[0].textContent).toContain('pronto');
    expect(cards[0].textContent).not.toContain('Excluir');
    expect(cards[1].textContent).toContain('sem rede');
    expect(cards[1].textContent).toContain('Excluir');
  });

  it('constrói a imagem na máquina escolhida e mostra o log; se já existia, oferece reconstruir', () => {
    open([profile()]);

    click('Construir imagem');
    http.expectOne({ method: 'POST', url: '/api/stack-profiles/s1/build?environmentId=e1&force=false' })
      .flush({ ok: true, image: 'agentforge/stack:abc', built: true, log: 'Step 1/1 : FROM node:22' });
    fixture.detectChanges();
    const result = root().querySelector('[data-testid="build-result"]')!;
    expect(result.textContent).toContain('imagem construída');
    expect(result.textContent).toContain('Step 1/1');

    click('Construir imagem');
    http.expectOne('/api/stack-profiles/s1/build?environmentId=e1&force=false')
      .flush({ ok: true, image: 'agentforge/stack:abc', built: false, log: '' });
    fixture.detectChanges();
    expect(root().querySelector('[data-testid="build-result"]')!.textContent).toContain('imagem já existia');

    click('Reconstruir');
    http.expectOne('/api/stack-profiles/s1/build?environmentId=e1&force=true')
      .flush({ ok: true, image: 'agentforge/stack:abc', built: true, log: 'refeita' });
  });

  it('falha de construção (máquina fora do ar) aparece como erro com o motivo', () => {
    open([profile()]);

    click('Construir imagem');
    http.expectOne('/api/stack-profiles/s1/build?environmentId=e1&force=false')
      .flush({ ok: false, image: '', built: false, log: 'Host agent inacessível em http://192.168.0.20:8787/' });
    fixture.detectChanges();

    const result = root().querySelector('[data-testid="build-result"]')!;
    expect(result.textContent).toContain('falhou');
    expect(result.textContent).toContain('Host agent inacessível');
  });

  it('sem ambientes não dá para construir (o botão fica desabilitado)', () => {
    open([profile()], []);

    const b = Array.from(root().querySelectorAll('button')).find((x) => x.textContent?.includes('Construir imagem'))!;
    expect(b.disabled).toBeTrue();
  });

  it('cria um perfil com Dockerfile e limites', () => {
    open([]);

    click('Novo perfil');
    const page = fixture.componentInstance;
    page.draft = { name: 'Meu Go', description: '', image: '', dockerfile: 'FROM golang:1.23', network: 'none', cpuLimit: 1.5, memoryMb: 2048 };
    page.save();

    const req = http.expectOne({ method: 'POST', url: '/api/stack-profiles' });
    expect(req.request.body).toEqual({ name: 'Meu Go', description: '', image: '', dockerfile: 'FROM golang:1.23', network: 'none', cpuLimit: 1.5, memoryMb: 2048 });
    req.flush(profile({ id: 's9', name: 'Meu Go', builtIn: false }));
    http.expectOne('/api/stack-profiles').flush([]);
  });
});
