export type DiffKind = 'add' | 'del' | 'hunk' | 'meta' | 'ctx';
export interface DiffLine {
  kind: DiffKind;
  text: string;
}

/** Classifica cada linha de um `git diff` para colorir na tela. */
export function parseDiff(diff: string): DiffLine[] {
  if (!diff) return [];
  return diff.replace(/\r\n/g, '\n').replace(/\n$/, '').split('\n').map((text) => {
    if (text.startsWith('+++') || text.startsWith('---') || text.startsWith('diff ') || text.startsWith('index ')
      || text.startsWith('new file') || text.startsWith('deleted file') || text.startsWith('similarity')
      || text.startsWith('rename ') || text.startsWith('Binary files')) return { kind: 'meta', text };
    if (text.startsWith('@@')) return { kind: 'hunk', text };
    if (text.startsWith('+')) return { kind: 'add', text };
    if (text.startsWith('-')) return { kind: 'del', text };
    return { kind: 'ctx', text };
  });
}

export function diffStats(lines: DiffLine[]): { added: number; removed: number } {
  return {
    added: lines.filter((l) => l.kind === 'add').length,
    removed: lines.filter((l) => l.kind === 'del').length,
  };
}
