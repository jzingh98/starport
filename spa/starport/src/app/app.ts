import { HttpClient } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterOutlet, Routes } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './core/auth.service';
import { WorkspaceService } from './core/workspace.service';
import { GameDetailPage, PlayPage, type GameCard } from './player.page';
import { MySavesPage } from './my-saves.page';
import { PublishPage } from './publish.page';
import { ReviewPage } from './review.page';
import { StudioGamePage } from './studio-game.page';

@Component({
  selector: 'app-games',
  template: `
    <section class="mx-auto max-w-6xl px-6 py-16">
      <p class="text-accent-fg text-xs font-semibold uppercase tracking-[0.22em]">
        Your next world awaits
      </p>
      <h1
        class="font-display mt-4 max-w-3xl text-5xl font-semibold tracking-tight text-fg md:text-7xl"
      >
        A home for games worth exploring.
      </h1>
      <p class="mt-6 max-w-2xl text-lg text-fg-2">
        Play games from independent creators, save your progress, and return whenever you like.
      </p>
      <div class="mt-8 flex flex-wrap gap-3">
        <a class="btn btn-primary" href="#games">Explore games</a>
        <a class="btn btn-secondary" routerLink="/publish">Publish your game</a>
      </div>
      <div id="games" class="mt-20 border-t border-line pt-8">
        <div class="mb-6 flex items-baseline justify-between gap-4">
          <h2 class="font-display text-2xl font-semibold text-fg">Browse games</h2>
          <span class="text-sm text-fg-3">Free to play</span>
        </div>
        @if (loading()) {
          <p class="text-fg-2">Loading games…</p>
        } @else if (error()) {
          <p role="alert" class="text-danger-fg">{{ error() }}</p>
          <button type="button" class="btn btn-secondary mt-4" (click)="retry()">Try again</button>
        } @else if (games().length === 0) {
          <div class="card max-w-2xl p-8">
            <h3 class="font-display text-xl font-semibold text-fg">The catalog is taking shape</h3>
            <p class="mt-2 text-fg-2">Public games will appear here after review.</p>
          </div>
        } @else {
          <div class="grid gap-4 md:grid-cols-2 lg:grid-cols-3">
            @for (game of games(); track game.id) {
              <article class="card p-6">
                <h3 class="font-display text-xl font-semibold text-fg">
                  <a [routerLink]="['/games', game.id]" class="hover:text-accent-fg">{{
                    game.title
                  }}</a>
                </h3>
                <p class="mt-2 text-fg-2">{{ game.description }}</p>
                <a
                  class="mt-5 inline-block text-sm font-semibold text-accent-fg"
                  [routerLink]="['/games', game.id]"
                  >View game →</a
                >
              </article>
            }
          </div>
          @if (moreError()) {
            <p class="mt-6 text-sm text-danger-fg" role="alert">{{ moreError() }}</p>
          }
          @if (hasMore()) {
            <div class="mt-8 text-center">
              <button
                type="button"
                class="btn btn-secondary"
                (click)="loadMore()"
                [disabled]="loadingMore()"
              >
                {{ loadingMore() ? 'Loading…' : 'Load more games' }}
              </button>
            </div>
          }
        }
      </div>
    </section>
  `,
  imports: [RouterLink],
})
export class GamesPage implements OnInit {
  private readonly http = inject(HttpClient);
  protected readonly games = signal<GameCard[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly loadingMore = signal(false);
  protected readonly moreError = signal('');
  protected readonly hasMore = signal(false);
  private page = 0;

  ngOnInit(): void {
    void this.loadPage(0);
  }

  protected retry(): void {
    this.loading.set(true);
    void this.loadPage(0);
  }

  protected loadMore(): void {
    if (!this.hasMore() || this.loadingMore()) return;
    void this.loadPage(this.page + 1);
  }

  private async loadPage(page: number): Promise<void> {
    if (page > 0) this.loadingMore.set(true);
    this.error.set('');
    this.moreError.set('');
    try {
      const games = await firstValueFrom(this.http.get<GameCard[]>(`/api/games?page=${page}`));
      this.page = page;
      if (page === 0) this.games.set(games);
      else this.games.update((existing) => [...existing, ...games]);
      this.hasMore.set(games.length === 24 && page < 100);
    } catch {
      if (page === 0) this.error.set('Games are unavailable right now. Please try again later.');
      else this.moreError.set('More games could not be loaded. Please try again.');
    } finally {
      this.loading.set(false);
      this.loadingMore.set(false);
    }
  }
}

@Component({
  selector: 'app-auth-callback',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-xl px-6 py-24 text-center">
      <h1 class="font-display text-3xl font-semibold text-fg">Signing you in</h1>
      @if (error()) {
        <p class="mt-4 text-danger-fg" role="alert">{{ error() }}</p>
        <a class="btn btn-secondary mt-6" routerLink="/">Return to games</a>
      } @else {
        <p class="mt-4 text-fg-2">Returning you to Starport…</p>
      }
    </section>
  `,
})
export class AuthCallbackPage implements OnInit {
  private readonly auth = inject(AuthService);
  private readonly workspaces = inject(WorkspaceService);
  private readonly router = inject(Router);
  protected readonly error = signal('');

  ngOnInit(): void {
    void this.finish();
  }

  private async finish(): Promise<void> {
    try {
      const returnUrl = await this.auth.completeSignIn();
      await this.workspaces.reload();
      await this.router.navigateByUrl(returnUrl);
    } catch (error) {
      console.error('[Auth] Sign-in callback failed', error);
      this.error.set('Sign-in could not be completed. Please try again.');
    }
  }
}

export const routes: Routes = [
  { path: '', component: GamesPage },
  { path: 'publish', component: PublishPage },
  { path: 'publish/workspaces/:workspaceId/games/:gameId', component: StudioGamePage },
  { path: 'review', component: ReviewPage },
  { path: 'me/saves', component: MySavesPage },
  { path: 'games/:gameId/play', component: PlayPage },
  { path: 'games/:gameId', component: GameDetailPage },
  { path: 'auth/callback', component: AuthCallbackPage },
  { path: '**', redirectTo: '' },
];

@Component({
  selector: 'app-root',
  template: `
    <header class="border-b border-line bg-surface/80">
      <nav
        aria-label="Main navigation"
        class="mx-auto flex max-w-6xl flex-wrap items-center justify-between gap-4 px-6 py-4"
      >
        <a routerLink="/" class="font-display text-xl font-bold tracking-tight text-fg"
          >✦ Starport</a
        >
        <div class="flex items-center gap-6 text-sm">
          <a routerLink="/" class="text-fg-2 hover:text-fg">Games</a>
          <a routerLink="/publish" class="text-fg-2 hover:text-fg">Publish</a>
          @if (auth.isModerator()) {
            <a routerLink="/review" class="text-fg-2 hover:text-fg">Review</a>
          }
          @if (auth.isAuthenticated()) {
            <a routerLink="/me/saves" class="text-fg-2 hover:text-fg">My saves</a>
            @if (workspaces.workspaces().length > 0) {
              <label class="sr-only" for="workspace-switcher">Workspace</label>
              <select
                id="workspace-switcher"
                class="rounded-lg border border-line bg-surface px-2 py-1.5 text-fg"
                [value]="workspaces.current()?.id ?? ''"
                (change)="selectWorkspace($event)"
              >
                @for (workspace of workspaces.workspaces(); track workspace.id) {
                  <option [value]="workspace.id">{{ workspace.name }}</option>
                }
              </select>
            }
            @if (auth.mode === 'oidc') {
              <span class="hidden text-fg-3 sm:inline">{{ auth.name() }}</span>
              <button type="button" class="text-fg-2 hover:text-fg" (click)="signOut()">
                Sign out
              </button>
            } @else {
              <span class="text-fg-3">Local mode</span>
            }
          } @else if (!auth.loading()) {
            <button type="button" class="btn btn-secondary" (click)="signIn()">Sign in</button>
          }
        </div>
      </nav>
    </header>
    <main><router-outlet /></main>
  `,
  imports: [RouterLink, RouterOutlet],
})
export class App implements OnInit {
  protected readonly auth = inject(AuthService);
  protected readonly workspaces = inject(WorkspaceService);

  ngOnInit(): void {
    void this.auth.init().then(() => {
      if (this.auth.isAuthenticated()) return this.workspaces.ensureLoaded();
      return undefined;
    });
  }

  protected selectWorkspace(event: Event): void {
    this.workspaces.select((event.target as HTMLSelectElement).value);
  }

  protected signIn(): void {
    void this.auth.login();
  }

  protected signOut(): void {
    void this.auth.logout().then(() => this.workspaces.clear());
  }
}
