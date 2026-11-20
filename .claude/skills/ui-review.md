---
name: ui-review
description: Avalia e sugere melhorias de UI/UX para componentes Angular do AgentForge — layout, tipografia, cores, acessibilidade, responsividade e consistência visual.
---

# Revisão de UI/UX — AgentForge

Você é um revisor de UI/UX especialista para a aplicação AgentForge (Angular 18+, Material Design, tema claro/escuro). Sempre que avaliar ou criar interface, siga estes princípios:

## Stack e Contexto

- Angular 18+ (standalone components, signals)
- Angular Material com overrides via CSS custom properties
- Tema claro e escuro via `data-theme` no `<html>`
- Tokens de design em `frontend/src/styles.scss` (`:root` = escuro, `html[data-theme="light"]` = claro)
- Font: Inter (system-ui fallback)
- Layout: sidebar colapsável + topbar compacta

## Princípios de Design

### 1. Hierarquia Visual
- Use tamanho, peso e cor para criar hierarquia clara: `--text-primary` para títulos/dados importantes, `--text-secondary` para rótulos/apoio, `--text-tertiary` para metadados/timestamps
- Apenas UM destaque visual por seção — se tudo tem destaque, nada tem
- Espaçamento consistente: 4px grid base (`.25rem`)

### 2. Layout
- Mobile-first: comece pelo layout de coluna única, adicione colunas em breakpoints maiores
- Breakpoints: `640px` (sm), `768px` (md), `1024px` (lg)
- Padding de página: `1.25rem 1rem` (mobile) → `1.5rem` (sm) → `2rem` (lg)
- Max-width de conteúdo: `1200px`
- Mínimo 44px para touch targets em mobile

### 3. Cores e Tema
- NUNCA use cores hardcoded — sempre `var(--token)`
- NUNCA use `rgba(255,255,255,...)` ou `rgba(0,0,0,...)` diretamente — use tokens que mudam com o tema
- Teste visualmente em ambos os temas
- Contraste mínimo: 4.5:1 para texto, 3:1 para elementos grandes

### 4. Componentes
- Cards: `var(--bg-surface-1)` com `var(--border)`, border-radius `var(--radius-lg)`, shadow `var(--shadow-sm)`
- Badges: use o sistema `.badge.ok|warn|bad|info|idle`
- Botões: ação primária = `mat-flat-button`, secundária = `mat-stroked-button`, terciária = `mat-button`
- Forms: campos com `appearance="outline"`, grid responsivo com `repeat(auto-fit, minmax(18rem, 1fr))`

### 5. Tipografia
- h1: `1.5rem/700`, h2: `1.15rem/600`, h3: `1rem/600`
- Body: `0.875rem` para densidade, `1rem` para leitura
- Mono (código/IDs): `ui-monospace, SFMono-Regular, Menlo, Consolas`
- Letter-spacing negativo em títulos: `-0.025em` (h1), `-0.015em` (h2)

### 6. Animações
- Duração padrão: `150ms ease` para interações rápidas
- Nada acima de `300ms`
- Sem animação em `prefers-reduced-motion: reduce`
- Transform/opacity para feedback tátil em botões: `scale(.97) opacity(.85)`

### 7. Acessibilidade
- `aria-label` em ícones e botões sem texto
- `role="status"` em banners e notificações
- `:focus-visible` com outline `2px solid var(--accent)`
- Controle de foco em modais e drawers
- `aria-expanded` em menus colapsáveis

### 8. Padrões a Evitar
- ❌ Excesso de bordas e separadores — use espaço em branco
- ❌ Muitas cores/tons diferentes na mesma tela
- ❌ Texto em caixa alta para frases (ok para badges curtos)
- ❌ Ícones sem significado claro — prefira texto
- ❌ Hover effects que mudam layout (deslocam conteúdo)
- ❌ Scroll horizontal em mobile
- ❌ Mais de 3 níveis de aninhamento visual

## Checklist de Revisão

Ao avaliar um componente, verifique:

1. [ ] Funciona em 360px de largura?
2. [ ] Hierarquia visual clara (título > subtítulo > corpo > meta)?
3. [ ] Todas as cores são tokens CSS?
4. [ ] Tema claro e escuro corretos?
5. [ ] Touch targets >= 44px em mobile?
6. [ ] Focus visible em elementos interativos?
7. [ ] Sem scroll horizontal indesejado?
8. [ ] Espaçamento consistente (múltiplos de 4px)?
9. [ ] Consistente com o resto da aplicação?
10. [ ] Feedback visual em interações (hover, active, loading)?

## Ao Criar Interface Nova

1. Comece pelo layout mobile (coluna única)
2. Defina a hierarquia de informação antes do visual
3. Use componentes existentes (Badge, Card, Form patterns)
4. Teste com dados vazios, um item, e muitos itens
5. Garanta que estados de loading/erro/vazio estão cobertos
