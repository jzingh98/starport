import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './core/auth.service';
import { RUNTIME_CONFIG } from './core/runtime-config';
import { WorkspaceService } from './core/workspace.service';

interface StudioGame {
  id: string;
  slug: string;
  title: string;
  description: string;
  audience: 'Private' | 'People' | 'Team' | 'Public';
  isPublished: boolean;
  hasValidatedRelease: boolean;
}

interface WorkspaceUsage {
  games: number;
  publishedGames: number;
  retainedBytes: number;
  deliveredBytesThisMonth: number;
  limits: Record<string, number | null>;
  paidThrough: string | null;
}

@Component({
  selector: 'app-publish',
  imports: [FormsModule, RouterLink],
  template: `
    <section class="mx-auto max-w-6xl px-6 py-16">
      <p class="text-accent-fg text-xs font-semibold uppercase tracking-[0.22em]">Creator studio</p>
      <h1 class="font-display mt-4 text-5xl font-semibold text-fg">Publish on Starport.</h1>
      <p class="mt-5 max-w-2xl text-lg text-fg-2">
        Create a space for your Godot browser game, choose who can play, and share a private preview
        before requesting a public listing.
      </p>

      @if (auth.loading() || workspaces.status() === 'loading') {
        <p class="mt-10 text-fg-2">Loading your studio…</p>
      } @else if (!auth.isAuthenticated()) {
        <div class="card mt-10 max-w-2xl p-8">
          <h2 class="font-display text-2xl font-semibold text-fg">Sign in to publish</h2>
          <p class="mt-3 text-fg-2">
            Playing public games is free. A Pro or Team subscription lets you publish your own
            games.
          </p>
          <button type="button" class="btn btn-primary mt-6" (click)="signIn()">Sign in</button>
        </div>
      } @else if (workspaces.status() === 'error') {
        <div class="card mt-10 max-w-2xl p-8">
          <p role="alert" class="text-danger-fg">Your workspaces could not be loaded.</p>
          <button type="button" class="btn btn-secondary mt-5" (click)="reloadWorkspaces()">
            Try again
          </button>
        </div>
      } @else if (workspaces.workspaces().length === 0) {
        <div class="card mt-10 max-w-2xl p-8">
          <h2 class="font-display text-2xl font-semibold text-fg">No workspace yet</h2>
          @if (auth.mode === 'local') {
            <p class="mt-3 text-fg-2">
              Create a personal workspace to get started in this local Starport.
            </p>
            <form class="mt-6" (ngSubmit)="createWorkspace()" #workspaceForm="ngForm">
              <label class="block text-sm font-medium text-fg" for="workspace-name"
                >Workspace name</label
              >
              <input
                id="workspace-name"
                name="workspaceName"
                class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                required
                maxlength="200"
                [(ngModel)]="workspaceName"
                (ngModelChange)="onWorkspaceNameChange()"
                placeholder="My workspace"
              />
              <label class="mt-5 block text-sm font-medium text-fg" for="workspace-slug"
                >URL name</label
              >
              <input
                id="workspace-slug"
                name="workspaceSlug"
                class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                required
                maxlength="80"
                pattern="[a-z0-9]+(?:-[a-z0-9]+)*"
                [(ngModel)]="workspaceSlug"
                (ngModelChange)="workspaceSlugEdited.set(true)"
                placeholder="my-workspace"
              />
              @if (workspaceError()) {
                <p class="mt-4 text-danger-fg" role="alert">{{ workspaceError() }}</p>
              }
              <button
                type="submit"
                class="btn btn-primary mt-6"
                [disabled]="workspaceForm.invalid || creatingWorkspace()"
              >
                {{ creatingWorkspace() ? 'Creating…' : 'Create workspace' }}
              </button>
            </form>
          } @else {
            <p class="mt-3 text-fg-2">
              Create or join a workspace in your account center to start publishing.
            </p>
            @if (config.auth.accountUrl) {
              <a class="btn btn-primary mt-6" [href]="config.auth.accountUrl"
                >Open account center</a
              >
            }
          }
        </div>
      } @else {
        <div
          class="mt-10 flex flex-wrap items-center justify-between gap-4 border-b border-line pb-6"
        >
          <div>
            <p class="text-xs font-semibold uppercase tracking-wider text-fg-3">
              Current workspace
            </p>
            <h2 class="font-display mt-1 text-2xl font-semibold text-fg">
              {{ workspaces.current()?.name }}
            </h2>
            <p class="mt-1 text-sm text-fg-3">
              {{ workspaces.current()?.kind }} · {{ workspaces.current()?.role }}
            </p>
          </div>
          @if (workspaces.workspaces().length > 1) {
            <div>
              <label class="block text-sm font-medium text-fg-2" for="studio-workspace"
                >Switch workspace</label
              >
              <select
                id="studio-workspace"
                class="mt-1 rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                [value]="workspaces.current()?.id ?? ''"
                (change)="selectWorkspace($event)"
              >
                @for (workspace of workspaces.workspaces(); track workspace.id) {
                  <option [value]="workspace.id">{{ workspace.name }}</option>
                }
              </select>
            </div>
          }
        </div>
        @if (usage(); as currentUsage) {
          <div class="card mt-8 p-6">
            <h2 class="font-display text-xl font-semibold text-fg">Workspace usage</h2>
            <div class="mt-4 grid gap-5 text-sm sm:grid-cols-2 lg:grid-cols-4">
              <div>
                <p class="text-fg-3">Games</p>
                <p class="mt-1 font-semibold text-fg">
                  {{ currentUsage.games }} / {{ usageLimit(currentUsage, 'games') }}
                </p>
              </div>
              <div>
                <p class="text-fg-3">Published</p>
                <p class="mt-1 font-semibold text-fg">
                  {{ currentUsage.publishedGames }} /
                  {{ usageLimit(currentUsage, 'published_games') }}
                </p>
              </div>
              <div>
                <p class="text-fg-3">Stored releases</p>
                <p class="mt-1 font-semibold text-fg">
                  {{ toMiB(currentUsage.retainedBytes) }} /
                  {{ usageLimit(currentUsage, 'retained_bytes_mb') }} MiB
                </p>
              </div>
              <div>
                <p class="text-fg-3">Delivery this month</p>
                <p class="mt-1 font-semibold text-fg">
                  {{ toMiB(currentUsage.deliveredBytesThisMonth) }} /
                  {{ usageLimit(currentUsage, 'monthly_delivery_mb') }} MiB
                </p>
              </div>
            </div>
            @if (currentUsage.limits['upload_bytes_mb'] != null) {
              <p class="mt-4 text-xs text-fg-3">
                Maximum ZIP upload: {{ currentUsage.limits['upload_bytes_mb'] }} MiB
              </p>
            }
          </div>
        } @else if (usageError()) {
          <p class="mt-6 text-sm text-fg-3">Workspace usage is temporarily unavailable.</p>
        }
        @if (!workspaces.canPublish()) {
          <div class="card mt-8 max-w-2xl p-8">
            <h2 class="font-display text-2xl font-semibold text-fg">
              Publishing needs Pro or Team
            </h2>
            <p class="mt-3 text-fg-2">
              You can play public games with a free account. Upgrade this workspace to publish
              games. Paid team members can publish in a Team workspace; Viewers cannot.
            </p>
            @if (config.auth.accountUrl) {
              <a class="btn btn-primary mt-6 mr-3" [href]="config.auth.accountUrl">Manage plan</a>
            }
            <a class="btn btn-secondary mt-6" routerLink="/">Browse games</a>
          </div>
        }
        @if (workspaces.current()?.role !== 'Viewer') {
          <div class="mt-10 grid gap-10 lg:grid-cols-[minmax(0,1fr)_minmax(20rem,0.8fr)]">
            <div>
              <div class="mb-5 flex items-baseline justify-between gap-3">
                <h2 class="font-display text-2xl font-semibold text-fg">Your games</h2>
                <span class="text-sm text-fg-3">{{ games().length }} total</span>
              </div>
              @if (loadingGames()) {
                <p class="text-fg-2">Loading games…</p>
              } @else if (gamesError()) {
                <p class="text-danger-fg" role="alert">{{ gamesError() }}</p>
                <button type="button" class="btn btn-secondary mt-4" (click)="reloadGames()">
                  Try again
                </button>
              } @else if (games().length === 0) {
                <div class="card p-8">
                  <h3 class="font-display text-xl font-semibold text-fg">Create your first game</h3>
                  <p class="mt-2 text-fg-2">
                    Your draft stays private while you prepare a browser build and choose its
                    audience.
                  </p>
                </div>
              } @else {
                <div class="space-y-4">
                  @for (game of games(); track game.id) {
                    <article class="card p-6">
                      <div class="flex flex-wrap items-start justify-between gap-3">
                        <h3 class="font-display text-xl font-semibold text-fg">{{ game.title }}</h3>
                        <span class="rounded-full border border-line px-3 py-1 text-xs text-fg-2">{{
                          game.isPublished ? 'Published' : 'Draft'
                        }}</span>
                      </div>
                      <p class="mt-2 text-fg-2">{{ game.description || 'No description yet.' }}</p>
                      <p class="mt-4 text-xs text-fg-3">
                        {{ game.audience }} access ·
                        {{ game.hasValidatedRelease ? 'Build ready' : 'No build yet' }}
                      </p>
                      <a
                        class="mt-5 inline-block text-sm font-semibold text-accent-fg"
                        [routerLink]="[
                          '/publish/workspaces',
                          workspaces.current()?.id,
                          'games',
                          game.id,
                        ]"
                      >
                        Manage game →
                      </a>
                    </article>
                  }
                </div>
              }
            </div>
            @if (workspaces.canPublish()) {
              <form class="card h-fit p-7" (ngSubmit)="createGame()" #gameForm="ngForm">
                <h2 class="font-display text-2xl font-semibold text-fg">New game draft</h2>
                <p class="mt-2 text-sm text-fg-2">
                  Start with the basics, then add a build and choose who can play.
                </p>
                <label class="mt-6 block text-sm font-medium text-fg" for="game-title">Title</label>
                <input
                  id="game-title"
                  name="title"
                  class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                  required
                  maxlength="120"
                  [(ngModel)]="title"
                  (ngModelChange)="onTitleChange()"
                  placeholder="Your game title"
                />
                <label class="mt-5 block text-sm font-medium text-fg" for="game-slug"
                  >URL name</label
                >
                <input
                  id="game-slug"
                  name="slug"
                  class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                  required
                  maxlength="80"
                  pattern="[a-z0-9]+(?:-[a-z0-9]+)*"
                  [(ngModel)]="slug"
                  (ngModelChange)="slugEdited.set(true)"
                  placeholder="your-game"
                />
                <p class="mt-1 text-xs text-fg-3">Lowercase letters, numbers, and hyphens.</p>
                <label class="mt-5 block text-sm font-medium text-fg" for="game-description"
                  >Description</label
                >
                <textarea
                  id="game-description"
                  name="description"
                  rows="4"
                  maxlength="2000"
                  class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                  [(ngModel)]="description"
                  placeholder="What will players discover?"
                ></textarea>
                @if (createError()) {
                  <p class="mt-4 text-sm text-danger-fg" role="alert">{{ createError() }}</p>
                }
                @if (createdMessage()) {
                  <p class="mt-4 text-sm text-success-fg" role="status">{{ createdMessage() }}</p>
                }
                <button
                  type="submit"
                  class="btn btn-primary mt-6"
                  [disabled]="gameForm.invalid || saving()"
                >
                  {{ saving() ? 'Creating…' : 'Create draft' }}
                </button>
              </form>
            }
          </div>
        }
      }
    </section>
  `,
})
export class PublishPage implements OnInit {
  private readonly http = inject(HttpClient);
  protected readonly config = inject(RUNTIME_CONFIG);
  protected readonly auth = inject(AuthService);
  protected readonly workspaces = inject(WorkspaceService);
  protected readonly games = signal<StudioGame[]>([]);
  protected readonly usage = signal<WorkspaceUsage | null>(null);
  protected readonly usageError = signal(false);
  protected readonly loadingGames = signal(false);
  protected readonly gamesError = signal('');
  protected readonly saving = signal(false);
  protected readonly createError = signal('');
  protected readonly createdMessage = signal('');
  protected readonly slugEdited = signal(false);
  protected readonly workspaceSlugEdited = signal(false);
  protected readonly creatingWorkspace = signal(false);
  protected readonly workspaceError = signal('');
  protected title = '';
  protected slug = '';
  protected description = '';
  protected workspaceName = '';
  protected workspaceSlug = '';
  private requestNumber = 0;
  private usageRequestNumber = 0;
  private readonly refreshOnWorkspaceChange = effect(() => {
    const workspace = this.workspaces.current();
    if (this.workspaces.status() === 'ready' && workspace) {
      void this.loadGames();
      void this.loadUsage(workspace.id);
    }
  });

  ngOnInit(): void {
    void this.initialize();
  }

  private async initialize(): Promise<void> {
    await this.auth.init();
    if (!this.auth.isAuthenticated()) return;
    await this.workspaces.ensureLoaded();
  }

  protected signIn(): void {
    void this.auth.login('/publish');
  }
  protected reloadWorkspaces(): void {
    void this.workspaces.reload();
  }
  protected reloadGames(): void {
    void this.loadGames();
  }

  protected selectWorkspace(event: Event): void {
    this.workspaces.select((event.target as HTMLSelectElement).value);
  }

  private async loadGames(): Promise<void> {
    const workspace = this.workspaces.current();
    const request = ++this.requestNumber;
    this.games.set([]);
    this.gamesError.set('');
    if (!workspace || workspace.role === 'Viewer') {
      this.loadingGames.set(false);
      return;
    }
    this.loadingGames.set(true);
    try {
      const games = await firstValueFrom(
        this.http.get<StudioGame[]>(`/api/workspaces/${encodeURIComponent(workspace.id)}/games`)
      );
      if (request === this.requestNumber) this.games.set(games);
    } catch (error) {
      if (request === this.requestNumber) {
        console.error('[Studio] Could not load games', error);
        this.gamesError.set('Your games could not be loaded. Please try again.');
      }
    } finally {
      if (request === this.requestNumber) this.loadingGames.set(false);
    }
  }

  private async loadUsage(workspaceId: string): Promise<void> {
    const request = ++this.usageRequestNumber;
    this.usage.set(null);
    this.usageError.set(false);
    try {
      const usage = await firstValueFrom(
        this.http.get<WorkspaceUsage>(`/api/workspaces/${encodeURIComponent(workspaceId)}/usage`)
      );
      if (request === this.usageRequestNumber) this.usage.set(usage);
    } catch {
      if (request === this.usageRequestNumber) this.usageError.set(true);
    }
  }

  protected toMiB(bytes: number): string {
    return (bytes / 1048576).toFixed(1);
  }

  protected usageLimit(usage: WorkspaceUsage, key: string): string {
    return usage.limits[key]?.toLocaleString() ?? '∞';
  }

  protected onTitleChange(): void {
    if (!this.slugEdited()) this.slug = this.makeSlug(this.title);
  }

  protected onWorkspaceNameChange(): void {
    if (!this.workspaceSlugEdited()) this.workspaceSlug = this.makeSlug(this.workspaceName);
  }

  private makeSlug(value: string): string {
    return value
      .toLowerCase()
      .normalize('NFKD')
      .replace(/[\u0300-\u036f]/g, '')
      .replace(/[^a-z0-9]+/g, '-')
      .replace(/^-|-$/g, '')
      .slice(0, 80)
      .replace(/-$/, '');
  }

  protected async createWorkspace(): Promise<void> {
    if (this.auth.mode !== 'local' || this.creatingWorkspace()) return;
    this.creatingWorkspace.set(true);
    this.workspaceError.set('');
    try {
      await firstValueFrom(
        this.http.post('/api/workspaces', {
          name: this.workspaceName.trim(),
          slug: this.workspaceSlug.trim(),
          kind: 'Personal',
        })
      );
      await this.workspaces.reload();
    } catch (error) {
      this.workspaceError.set(
        error instanceof HttpErrorResponse && error.status === 409
          ? 'That workspace URL name is already in use.'
          : 'The workspace could not be created. Please try again.'
      );
    } finally {
      this.creatingWorkspace.set(false);
    }
  }

  protected async createGame(): Promise<void> {
    const workspaceId = this.workspaces.current()?.id;
    if (!workspaceId || !this.workspaces.canPublish() || this.saving()) return;
    this.saving.set(true);
    this.createError.set('');
    this.createdMessage.set('');
    try {
      const game = await firstValueFrom(
        this.http.post<StudioGame>(`/api/workspaces/${encodeURIComponent(workspaceId)}/games`, {
          slug: this.slug.trim(),
          title: this.title.trim(),
          description: this.description.trim(),
        })
      );
      if (this.workspaces.current()?.id === workspaceId) {
        this.games.update((games) => [game, ...games]);
        this.title = '';
        this.slug = '';
        this.description = '';
        this.slugEdited.set(false);
        this.createdMessage.set(`“${game.title}” is ready as a private draft.`);
        void this.loadUsage(workspaceId);
      }
    } catch (error) {
      this.createError.set(
        error instanceof HttpErrorResponse && error.status === 409
          ? 'That URL name is already in use in this workspace.'
          : 'The draft could not be created. Please try again.'
      );
    } finally {
      this.saving.set(false);
    }
  }
}
