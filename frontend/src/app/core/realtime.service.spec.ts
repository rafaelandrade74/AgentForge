import { provideHttpClient } from '@angular/common/http';
import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { RealtimeService } from './realtime.service';

@Component({ selector: 'app-host', standalone: true, template: '' })
class HostComponent {
  readonly projectId = signal<string | null>(null);
  constructor(realtime: RealtimeService) {
    realtime.bindProject(() => this.projectId());
  }
}

describe('RealtimeService.bindProject', () => {
  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient()] });
  });

  it('entra no grupo quando o id aparece e sai ao destruir, sem escrever signal dentro do effect (NG0600)', async () => {
    const realtime = TestBed.inject(RealtimeService);
    const join = spyOn(realtime, 'join').and.callThrough();
    const leave = spyOn(realtime, 'leave').and.callThrough();
    const errors: unknown[] = [];
    const originalError = console.error;
    console.error = (...args: unknown[]) => errors.push(args);

    try {
      const fixture = TestBed.createComponent(HostComponent);
      fixture.detectChanges();
      expect(join).not.toHaveBeenCalled();

      fixture.componentInstance.projectId.set('p1');
      fixture.detectChanges();
      expect(join).toHaveBeenCalledOnceWith('p1');

      // a conexão real falha (não há hub no servidor do Karma): o status volta a "disconnected"
      await new Promise((r) => setTimeout(r, 300));
      expect(realtime.status()).toBe('disconnected');

      fixture.destroy();
      expect(leave).toHaveBeenCalledOnceWith('p1');
      expect(errors.filter((e) => JSON.stringify(e).includes('NG0600'))).toEqual([]);
    } finally {
      console.error = originalError;
    }
  });
});
