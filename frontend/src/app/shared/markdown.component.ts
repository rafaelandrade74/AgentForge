import { Component, ViewEncapsulation, computed, input } from '@angular/core';
import { renderMarkdown } from '../core/markdown';

/** Mostra texto em Markdown formatado (o HTML é gerado e escapado por renderMarkdown; o Angular ainda o sanitiza). */
@Component({
  selector: 'app-markdown',
  standalone: true,
  encapsulation: ViewEncapsulation.None,
  template: `<div class="md" [innerHTML]="html()"></div>`,
  styles: `
    .md { line-height: 1.5; word-break: break-word; }
    .md > :first-child { margin-top: 0; }
    .md > :last-child { margin-bottom: 0; }
    .md h3, .md h4, .md h5, .md h6 { margin: 1rem 0 .4rem; line-height: 1.25; }
    .md h3 { font-size: 1.1rem; } .md h4 { font-size: 1rem; } .md h5, .md h6 { font-size: .9rem; color: #bbb; }
    .md p { margin: .5rem 0; }
    .md ul, .md ol { margin: .4rem 0; padding-left: 1.4rem; }
    .md li { margin: .15rem 0; }
    .md a { color: #8ab4f8; }
    .md code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: .85em; background: #262626; padding: .05rem .3rem; border-radius: 4px; }
    .md pre.md-code { margin: .6rem 0; padding: .6rem .75rem; background: #0d0d0d; border-radius: 6px; overflow: auto; max-height: 24rem; }
    .md pre.md-code code { background: none; padding: 0; font-size: .8rem; white-space: pre; }
    .md blockquote { margin: .5rem 0; padding: .1rem .8rem; border-left: 3px solid #444; color: #bbb; }
    .md hr { border: 0; border-top: 1px solid #333; margin: .9rem 0; }
    .md table { border-collapse: collapse; margin: .6rem 0; display: block; overflow-x: auto; max-width: 100%; }
    .md th, .md td { border: 1px solid #333; padding: .25rem .6rem; text-align: left; }
    .md th { background: #1b1b1b; }
  `,
})
export class MarkdownComponent {
  readonly source = input<string | null | undefined>('');
  /** false: links viram texto (para uso dentro de outro link). */
  readonly links = input(true);

  readonly html = computed(() => renderMarkdown(this.source() ?? '', { links: this.links() }));
}
