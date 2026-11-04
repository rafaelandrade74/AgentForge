import { renderMarkdown } from './markdown';

describe('renderMarkdown', () => {
  it('formata títulos (abaixo do h1/h2 da página), negrito, itálico e código inline', () => {
    const html = renderMarkdown('# Resumo\n\nFiz **tudo** e *quase* nada em `Foo.cs`.');
    expect(html).toContain('<h3>Resumo</h3>');
    expect(html).toContain('<strong>tudo</strong>');
    expect(html).toContain('<em>quase</em>');
    expect(html).toContain('<code>Foo.cs</code>');
  });

  it('lista com aninhamento e numerada', () => {
    const html = renderMarkdown('- a\n- b\n  - b1\n  - b2\n- c\n\n1. um\n2. dois');
    expect(html).toBe('<ul><li>a</li><li>b<ul><li>b1</li><li>b2</li></ul></li><li>c</li></ul>\n<ol><li>um</li><li>dois</li></ol>');
  });

  it('bloco de código preserva o conteúdo e escapa HTML; sem interpretar markdown dentro', () => {
    const html = renderMarkdown('```json\n{"ok": true, "x": "<b>**n**</b>"}\n```');
    expect(html).toContain('<pre class="md-code"><code>{&quot;ok&quot;: true, &quot;x&quot;: &quot;&lt;b&gt;**n**&lt;/b&gt;&quot;}</code></pre>');
    expect(html).not.toContain('<strong>');
  });

  it('bloco de código sem fechamento vai até o fim', () => {
    expect(renderMarkdown('```\nlinha 1\nlinha 2')).toContain('linha 1\nlinha 2</code></pre>');
  });

  it('tabela, citação e linha horizontal', () => {
    const html = renderMarkdown('| Arquivo | Status |\n|---|---|\n| a.cs | ok |\n\n> nota **importante**\n\n---');
    expect(html).toContain('<th>Arquivo</th><th>Status</th>');
    expect(html).toContain('<td>a.cs</td><td>ok</td>');
    expect(html).toContain('<blockquote><p>nota <strong>importante</strong></p></blockquote>');
    expect(html).toContain('<hr>');
  });

  it('quebras simples dentro do parágrafo viram <br>', () => {
    expect(renderMarkdown('linha 1\nlinha 2')).toBe('<p>linha 1<br>linha 2</p>');
  });

  it('links só http(s) e abrem em outra aba com rel seguro; podem ser desligados', () => {
    expect(renderMarkdown('[doc](https://exemplo.com/a?b=1&c=2)')).toBe(
      '<p><a href="https://exemplo.com/a?b=1&amp;c=2" target="_blank" rel="noopener noreferrer">doc</a></p>');
    expect(renderMarkdown('[x](javascript:alert(1))')).not.toContain('<a ');
    expect(renderMarkdown('[doc](https://exemplo.com)', { links: false })).toBe('<p>doc (https://exemplo.com)</p>');
  });

  it('nunca deixa passar HTML do texto (XSS)', () => {
    const html = renderMarkdown('<script>alert(1)</script> <img src=x onerror=alert(1)> **<b>x</b>**\n\n# <svg onload=alert(1)>\n\n- <iframe>\n\n| <a> |\n|---|\n| <u> |');
    expect(html).not.toMatch(/<script|<img|<svg|<iframe|<b>|<u>|<a>/);
    expect(html).toContain('&lt;script&gt;');
  });

  it('não vira itálico em snake_case nem multiplicação', () => {
    const html = renderMarkdown('use nome_do_campo e 2 * 3 * 4');
    expect(html).not.toContain('<em>');
  });

  it('texto vazio ou nulo não quebra', () => {
    expect(renderMarkdown('')).toBe('');
    expect(renderMarkdown(null as unknown as string)).toBe('');
  });

  it('saída típica de agente: texto + JSON final em bloco', () => {
    const html = renderMarkdown('Implementei o filtro.\n\n## Mudanças\n- `Api.cs`: novo endpoint\n\n```json\n{"summary":"ok"}\n```');
    expect(html).toContain('<h4>Mudanças</h4>');
    expect(html).toContain('<li><code>Api.cs</code>: novo endpoint</li>');
    expect(html).toContain('<pre class="md-code">');
  });
});
