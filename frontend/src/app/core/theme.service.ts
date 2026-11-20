import { Injectable, signal } from '@angular/core';

export type Theme = 'dark' | 'light';

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly current = signal<Theme>(this.stored());

  toggle(): void {
    const next: Theme = this.current() === 'dark' ? 'light' : 'dark';
    this.current.set(next);
    this.apply(next);
    try { localStorage.setItem('af-theme', next); } catch {}
  }

  init(): void { this.apply(this.current()); }

  private stored(): Theme {
    try {
      const v = localStorage.getItem('af-theme');
      if (v === 'light' || v === 'dark') return v;
    } catch {}
    return matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark';
  }

  private apply(t: Theme): void {
    document.documentElement.setAttribute('data-theme', t);
  }
}
