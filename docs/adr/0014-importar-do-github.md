# ADR 0014 — Importar projeto do GitHub (cadastra e já clona)

## Contexto
Cadastrar um projeto exigia colar a URL, acertar a branch e, se o repositório fosse privado, gerar a chave SSH e autorizá-la
no GitHub à mão (ADR 0013). A pessoa pediu poder importar direto do GitHub, com o clone já feito.

## Decisão
- `POST /api/github/repos` lista os repositórios que um token enxerga (até 300) e `POST /api/github/import` cadastra e clona.
  Só valem com o login (JWT); os tokens de acesso do MCP não os alcançam.
- **O token do GitHub nunca é guardado nem logado**: vem na requisição, é usado nas chamadas à API do GitHub e descartado. As
  mensagens de erro não o repetem.
- A importação usa a URL SSH para repositórios privados e a HTTPS para públicos; a branch base é a padrão do GitHub.
  Repositório público não precisa de token.
- Para privados (git local), gera a chave SSH do AgentForge se faltar e a registra como **deploy key somente leitura**
  (`read_only: true`; o AgentForge nunca faz push). "Chave já em uso" não é erro. Com `addDeployKey = false` (ou sem token) a
  resposta orienta a autorizar a chave à mão. No modo host agent usa-se o git do computador, com as credenciais dele.
- O clone acontece na hora (`EnsureRepositoryAsync`), com uma nova tentativa após 3 s se a deploy key acabou de ser criada.
  **Se o clone falhar, o projeto é removido** e a API devolve 502 com o motivo: erro de acesso aparece agora, não na primeira demanda.
- Repositório já cadastrado (mesma URL HTTPS ou SSH) devolve 409.
- Nginx: `/api/github/` com `proxy_read_timeout 10m`, porque o clone pode demorar.

## Consequências
- O escopo `repo` do token é amplo; a interface avisa que ele é usado só ali. Um token com escopo mínimo (fine-grained, com
  *Administration: write* e *Contents: read* no repositório) também serve.
- Os testes usam um GitHub simulado (`HttpMessageHandler`) e um repositório Git local como alvo do clone.
