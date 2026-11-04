/**
 * Markdown → HTML mínimo para a saída dos agentes (sem dependência nova). Tudo o que vem do texto é escapado primeiro; as
 * tags são geradas só pelo próprio código, e links aceitam apenas http(s). Cobre o que os agentes escrevem: títulos, listas
 * (com aninhamento), blocos de código, código inline, negrito/itálico, links, citações, tabelas e linhas horizontais.
 */
export interface MarkdownOptions {
  /** false: links viram texto (para uso dentro de outro link). */
  links?: boolean;
}

const esc = (s: string): string =>
  s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const FENCE = /^\s*```\s*([\w+#.-]+)?\s*$/;
const HEADING = /^(#{1,6})\s+(.+?)\s*#*\s*$/;
const RULE = /^\s*([-*_])(\s*\1){2,}\s*$/;
const LIST_ITEM = /^(\s*)([-*+]|\d+[.)])\s+(.*)$/;
const QUOTE = /^\s*>\s?(.*)$/;
const TABLE_SEPARATOR = /^\s*\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$/;

function inline(text: string, links: boolean): string {
  const codes: string[] = [];
  let s = esc(text).replace(/`([^`\n]+)`/g, (_m, c: string) => {
    codes.push(`<code>${c}</code>`);
    return `\u0000${codes.length - 1}\u0000`;
  });
  s = s.replace(/\[([^\]\n]+)\]\((https?:\/\/[^\s)]+)\)/g, (_m, label: string, url: string) =>
    links ? `<a href="${url}" target="_blank" rel="noopener noreferrer">${label}</a>` : `${label} (${url})`);
  s = s.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>').replace(/__([^_\n]+)__/g, '<strong>$1</strong>');
  s = s.replace(/(^|[^*\w])\*([^*\s][^*\n]*?)\*(?!\*)/g, '$1<em>$2</em>').replace(/(^|\W)_([^_\s][^_\n]*?)_(?!\w)/g, '$1<em>$2</em>');
  s = s.replace(/~~([^~\n]+)~~/g, '<del>$1</del>');
  // eslint-disable-next-line no-control-regex
  return s.replace(/\u0000(\d+)\u0000/g, (_m, i: string) => codes[Number(i)]);
}

interface Item { indent: number; ordered: boolean; text: string }

function renderList(items: Item[], links: boolean): string {
  let html = '';
  const stack: { indent: number; tag: string }[] = [];
  for (const it of items) {
    while (stack.length && it.indent < stack[stack.length - 1].indent) html += `</li></${stack.pop()!.tag}>`;
    const top = stack[stack.length - 1];
    if (!top || it.indent > top.indent) {
      const tag = it.ordered ? 'ol' : 'ul';
      html += `<${tag}>`;
      stack.push({ indent: it.indent, tag });
    } else {
      html += '</li>';
    }
    html += `<li>${inline(it.text, links)}`;
  }
  while (stack.length) html += `</li></${stack.pop()!.tag}>`;
  return html;
}

function cells(row: string): string[] {
  return row.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map((c) => c.trim());
}

function startsBlock(line: string, next: string | undefined): boolean {
  return FENCE.test(line) || HEADING.test(line) || RULE.test(line) || LIST_ITEM.test(line) || QUOTE.test(line)
    || (line.includes('|') && next !== undefined && TABLE_SEPARATOR.test(next) && next.includes('-'));
}

export function renderMarkdown(source: string, options: MarkdownOptions = {}): string {
  const links = options.links ?? true;
  const lines = (source ?? '').replace(/\r\n?/g, '\n').split('\n');
  const out: string[] = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];
    if (line.trim() === '') { i++; continue; }

    const fence = FENCE.exec(line);
    if (fence) {
      const code: string[] = [];
      i++;
      while (i < lines.length && !FENCE.test(lines[i])) code.push(lines[i++]);
      i++; // fecha o bloco (ou acabou o texto)
      out.push(`<pre class="md-code"><code>${esc(code.join('\n'))}</code></pre>`);
      continue;
    }

    const heading = HEADING.exec(line);
    if (heading) {
      const level = Math.min(heading[1].length + 2, 6); // a página já tem h1/h2
      out.push(`<h${level}>${inline(heading[2], links)}</h${level}>`);
      i++;
      continue;
    }

    if (RULE.test(line)) { out.push('<hr>'); i++; continue; }

    if (line.includes('|') && i + 1 < lines.length && TABLE_SEPARATOR.test(lines[i + 1]) && lines[i + 1].includes('-')) {
      const head = cells(line);
      i += 2;
      const rows: string[][] = [];
      while (i < lines.length && lines[i].includes('|') && lines[i].trim() !== '') rows.push(cells(lines[i++]));
      out.push(
        `<table><thead><tr>${head.map((c) => `<th>${inline(c, links)}</th>`).join('')}</tr></thead><tbody>` +
        rows.map((r) => `<tr>${r.map((c) => `<td>${inline(c, links)}</td>`).join('')}</tr>`).join('') +
        '</tbody></table>',
      );
      continue;
    }

    if (QUOTE.test(line)) {
      const inner: string[] = [];
      while (i < lines.length && QUOTE.test(lines[i])) inner.push(QUOTE.exec(lines[i++])![1]);
      out.push(`<blockquote>${renderMarkdown(inner.join('\n'), options)}</blockquote>`);
      continue;
    }

    if (LIST_ITEM.test(line)) {
      const items: Item[] = [];
      while (i < lines.length) {
        const m = LIST_ITEM.exec(lines[i]);
        if (m) {
          items.push({ indent: m[1].replace(/\t/g, '    ').length, ordered: /\d/.test(m[2]), text: m[3] });
          i++;
        } else if (lines[i].trim() !== '' && /^\s{2,}\S/.test(lines[i]) && items.length) {
          items[items.length - 1].text += ' ' + lines[i].trim(); // continuação do item
          i++;
        } else break;
      }
      out.push(renderList(items, links));
      continue;
    }

    const para: string[] = [];
    while (i < lines.length && lines[i].trim() !== '' && (para.length === 0 || !startsBlock(lines[i], lines[i + 1]))) para.push(lines[i++]);
    out.push(`<p>${para.map((p) => inline(p, links)).join('<br>')}</p>`);
  }

  return out.join('\n');
}
