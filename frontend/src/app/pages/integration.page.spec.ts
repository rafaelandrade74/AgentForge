import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { IntegrationPage } from './integration.page';

describe('IntegrationPage (MCP)', () => {
  let fixture: ComponentFixture<IntegrationPage>;
  let http: HttpTestingController;

  const text = () => (fixture.nativeElement as HTMLElement).textContent ?? '';
  const byTestId = (id: string) => (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations()] });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(IntegrationPage);
    fixture.detectChanges();
    http.expectOne('/api/tokens').flush([
      { id: 't1', name: 'Notebook', prefix: 'afk_AbCd', createdAt: '2026-01-01T10:00:00Z', lastUsedAt: null, revokedAt: null },
      { id: 't2', name: 'Antigo', prefix: 'afk_ZzZz', createdAt: '2026-01-01T10:00:00Z', lastUsedAt: '2026-01-02T10:00:00Z', revokedAt: '2026-01-03T10:00:00Z' },
    ]);
    http.expectOne({ method: 'POST', url: '/mcp' }).flush({
      result: { tools: [
        { name: 'list_projects', title: 'Listar', description: 'Lista os projetos.', annotations: { readOnlyHint: true } },
        { name: 'cancel_task', title: 'Cancelar', description: 'Cancela a tarefa.', annotations: { destructiveHint: true } },
      ] },
    });
    fixture.detectChanges();
  });

  afterEach(() => http.verify());

  it('mostra o endereço do MCP, os tokens (sem o valor) e as ferramentas com suas marcas', () => {
    expect(byTestId('mcp-url')?.textContent).toContain('/mcp');
    expect(text()).toContain('Notebook');
    expect(text()).toContain('afk_AbCd…');
    expect(text()).toContain('revogado');
    expect(text()).toContain('nunca');
    expect(text()).toContain('list_projects');
    expect(text()).toContain('só leitura');
    expect(text()).toContain('destrutiva');
  });

  it('cria o token, mostra o valor uma única vez com os comandos prontos e recarrega a lista', () => {
    const page = fixture.componentInstance;
    page.name = '  Claude Code  ';
    page.create();

    const req = http.expectOne({ method: 'POST', url: '/api/tokens' });
    expect(req.request.body).toEqual({ name: 'Claude Code' });
    req.flush({ id: 't3', name: 'Claude Code', prefix: 'afk_Novo', token: 'afk_NovoSegredoCompleto' });
    http.expectOne({ method: 'GET', url: '/api/tokens' }).flush([]);
    fixture.detectChanges();

    expect(byTestId('created-token')?.textContent).toContain('afk_NovoSegredoCompleto');
    expect(byTestId('cmd-claude')?.textContent).toContain('claude mcp add --transport http agentforge');
    expect(byTestId('cmd-claude')?.textContent).toContain('Authorization: Bearer afk_NovoSegredoCompleto');
    const json = JSON.parse(byTestId('cmd-json')!.textContent!);
    expect(json.mcpServers.agentforge.type).toBe('http');
    expect(json.mcpServers.agentforge.headers.Authorization).toBe('Bearer afk_NovoSegredoCompleto');
    expect(page.name).toBe('');
  });

  it('revoga um token e esconde o valor recém-criado se for o mesmo', () => {
    const page = fixture.componentInstance;
    page.created.set({ id: 't1', name: 'Notebook', prefix: 'afk_AbCd', token: 'afk_valor' });

    page.revoke({ id: 't1', name: 'Notebook', prefix: 'afk_AbCd', createdAt: '', lastUsedAt: null, revokedAt: null });

    http.expectOne({ method: 'DELETE', url: '/api/tokens/t1' }).flush(null, { status: 204, statusText: 'No Content' });
    http.expectOne({ method: 'GET', url: '/api/tokens' }).flush([]);
    expect(page.created()).toBeNull();
  });
});
