import { DatePipe, DOCUMENT } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './core/auth.service';

interface MySave {
  gameId: string;
  gameTitle: string;
  slot: string;
  revision: string;
  updatedAt: string;
}

@Component({
  selector: 'app-my-saves',
  imports: [DatePipe, RouterLink],
  template: `
    <section class="mx-auto max-w-5xl px-6 py-16">
      <a routerLink="/" class="text-sm text-fg-2 hover:text-fg">← Games</a>
      <p class="text-accent-fg mt-8 text-xs font-semibold uppercase tracking-[0.22em]">Your data</p>
      <h1 class="font-display mt-4 text-5xl font-semibold text-fg">My cloud saves</h1>
      <p class="mt-4 max-w-2xl text-fg-2">
        Your saves remain available to export or delete even if a game goes offline.
      </p>
      @if (auth.loading() || loading()) {
        <p class="mt-10 text-fg-2">Loading saves…</p>
      } @else if (!auth.isAuthenticated()) {
        <div class="card mt-10 p-7">
          <p class="text-fg-2">Sign in to see and manage your cloud saves.</p>
          <button type="button" class="btn btn-primary mt-5" (click)="signIn()">Sign in</button>
        </div>
      } @else {
        <div class="mt-10 flex flex-wrap items-center justify-between gap-3">
          <h2 class="font-display text-2xl font-semibold text-fg">Saved progress</h2>
          <button
            type="button"
            class="btn btn-secondary"
            (click)="exportSaves()"
            [disabled]="exporting()"
          >
            {{ exporting() ? 'Preparing export…' : 'Download all saves' }}
          </button>
        </div>
        @if (exportError()) {
          <p class="mt-4 text-sm text-danger-fg" role="alert">{{ exportError() }}</p>
        }
        @if (error()) {
          <div class="card mt-6 p-7">
            <p class="text-danger-fg" role="alert">{{ error() }}</p>
            <button type="button" class="btn btn-secondary mt-4" (click)="reload()">
              Try again
            </button>
          </div>
        } @else if (saves().length === 0) {
          <div class="card mt-6 p-8">
            <p class="text-fg-2">
              You have no cloud saves yet. Games that support saves will appear here after you play
              while signed in.
            </p>
          </div>
        } @else {
          <div class="mt-6 space-y-4">
            @for (save of saves(); track save.gameId + ':' + save.slot) {
              <article class="card flex flex-wrap items-start justify-between gap-5 p-6">
                <div>
                  <h3 class="font-display text-xl font-semibold text-fg">{{ save.gameTitle }}</h3>
                  <p class="mt-2 text-sm text-fg-2">
                    Slot <span class="font-mono">{{ save.slot }}</span> · Updated
                    {{ save.updatedAt | date: 'medium' }}
                  </p>
                </div>
                @if (confirmDeleteKey() === key(save)) {
                  <div class="text-right">
                    <p class="text-sm text-danger-fg">Delete this save permanently?</p>
                    <div class="mt-3 flex justify-end gap-2">
                      <button
                        type="button"
                        class="btn btn-secondary"
                        (click)="confirmDeleteKey.set(null)"
                      >
                        Cancel
                      </button>
                      <button
                        type="button"
                        class="btn btn-primary"
                        (click)="deleteSave(save)"
                        [disabled]="deletingKey() !== null"
                      >
                        {{ deletingKey() === key(save) ? 'Deleting…' : 'Delete save' }}
                      </button>
                    </div>
                  </div>
                } @else {
                  <button
                    type="button"
                    class="text-sm font-medium text-danger-fg"
                    (click)="confirmDeleteKey.set(key(save))"
                  >
                    Delete
                  </button>
                }
                @if (deleteError()[key(save)]) {
                  <p class="w-full text-sm text-danger-fg" role="alert">
                    {{ deleteError()[key(save)] }}
                  </p>
                }
              </article>
            }
          </div>
        }
      }
    </section>
  `,
})
export class MySavesPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  protected readonly auth = inject(AuthService);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly saves = signal<MySave[]>([]);
  protected readonly exporting = signal(false);
  protected readonly exportError = signal('');
  protected readonly confirmDeleteKey = signal<string | null>(null);
  protected readonly deletingKey = signal<string | null>(null);
  protected readonly deleteError = signal<Record<string, string>>({});

  ngOnInit(): void {
    void this.load();
  }

  protected key(save: MySave): string {
    return `${save.gameId}:${save.slot}`;
  }

  protected signIn(): void {
    void this.auth.login('/me/saves');
  }

  protected reload(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    await this.auth.init();
    if (!this.auth.isAuthenticated()) {
      this.loading.set(false);
      return;
    }
    try {
      this.saves.set(await firstValueFrom(this.http.get<MySave[]>('/api/me/saves')));
    } catch {
      this.error.set('Your saves could not be loaded. Please try again.');
    } finally {
      this.loading.set(false);
    }
  }

  protected async exportSaves(): Promise<void> {
    if (!this.auth.isAuthenticated() || this.exporting()) return;
    this.exporting.set(true);
    this.exportError.set('');
    try {
      const blob = await firstValueFrom(
        this.http.get('/api/me/saves/export', { responseType: 'blob' })
      );
      const view = this.document.defaultView;
      if (!view) throw new Error('Download unavailable');
      const url = view.URL.createObjectURL(blob);
      const link = this.document.createElement('a');
      link.href = url;
      link.download = 'starport-saves.json';
      this.document.body.appendChild(link);
      link.click();
      link.remove();
      view.setTimeout(() => view.URL.revokeObjectURL(url), 1000);
    } catch {
      this.exportError.set('Your saves could not be exported. Please try again.');
    } finally {
      this.exporting.set(false);
    }
  }

  protected async deleteSave(save: MySave): Promise<void> {
    const key = this.key(save);
    if (this.confirmDeleteKey() !== key || this.deletingKey()) return;
    this.deletingKey.set(key);
    this.deleteError.update((errors) => ({ ...errors, [key]: '' }));
    try {
      await firstValueFrom(
        this.http.delete(
          `/api/me/saves/${encodeURIComponent(save.gameId)}/${encodeURIComponent(save.slot)}`,
          {
            headers: new HttpHeaders({ 'If-Match': save.revision }),
          }
        )
      );
      this.saves.update((saves) => saves.filter((entry) => this.key(entry) !== key));
      this.confirmDeleteKey.set(null);
    } catch (error) {
      this.deleteError.update((errors) => ({
        ...errors,
        [key]:
          error instanceof HttpErrorResponse && error.status === 412
            ? 'This save changed. Refresh the list before deleting it.'
            : 'The save could not be deleted. Please try again.',
      }));
    } finally {
      this.deletingKey.set(null);
    }
  }
}
