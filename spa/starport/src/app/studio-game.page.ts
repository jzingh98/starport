import { DatePipe } from '@angular/common';
import { HttpClient, HttpErrorResponse, HttpEventType, HttpResponse } from '@angular/common/http';
import { DOCUMENT, Component, DestroyRef, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { filter, firstValueFrom, tap } from 'rxjs';
import { AuthService } from './core/auth.service';
import { RUNTIME_CONFIG } from './core/runtime-config';
import { WorkspaceService } from './core/workspace.service';

type Audience = 'Private' | 'People' | 'Team' | 'Public';
type UploadStatus = 'Pending' | 'Processing' | 'Ready' | 'Failed';

interface StudioGame {
  id: string;
  workspaceId: string;
  slug: string;
  title: string;
  description: string;
  audience: Audience;
  isPublished: boolean;
  hasValidatedRelease: boolean;
  publicReviewApproved: boolean;
  isModerationSuspended: boolean;
  metadataRevision: number;
  activeReleaseId: string | null;
}

interface GameRelease {
  id: string;
  createdAt: string;
  sha256: string;
  reviewStatus: string;
  reviewReason?: string | null;
}

interface GameUpload {
  id: string;
  gameId: string;
  status: UploadStatus;
  archiveBytes: number;
  releaseId: string | null;
  errorCode: string | null;
}

interface PersonShare {
  subject: string;
  email: string | null;
  expiresAt: string | null;
}

const MAX_ARCHIVE_BYTES = 256 * 1024 * 1024;

function apiError(error: unknown, fallback: string): string {
  if (error instanceof HttpErrorResponse) {
    const code = (error.error as { code?: string } | null)?.code;
    switch (code) {
      case 'publishing-plan-required':
        return 'Publishing access has expired. Manage your plan to continue.';
      case 'game-conflict':
        return 'This game changed in another session. Reload the page before trying again.';
      case 'review-required':
        return 'This release and its current details need public review before activation or publication.';
      case 'release-required':
        return 'Upload and activate a validated release first.';
      case 'active-release':
        return 'Activate another release before removing this one.';
      case 'audience-required':
        return 'Choose People, Team, or Public access first.';
      case 'player-not-found':
        return 'That player does not have a Starport account yet.';
      case 'share-limit-reached':
        return 'This game has reached its person-sharing limit.';
      case 'upload-too-large':
        return 'The ZIP exceeds your workspace upload limit.';
      case 'zip-required':
        return 'Choose a ZIP package exported for the browser.';
    }
  }
  return fallback;
}

@Component({
  selector: 'app-studio-game',
  imports: [DatePipe, FormsModule, RouterLink],
  template: `
    <section class="mx-auto max-w-6xl px-6 py-12">
      <a routerLink="/publish" class="text-sm text-fg-2 hover:text-fg">← Creator studio</a>
      @if (loading()) {
        <p class="mt-12 text-fg-2">Loading game…</p>
      } @else if (pageError()) {
        <div class="card mt-10 p-8">
          <h1 class="font-display text-3xl font-semibold text-fg">Game unavailable</h1>
          <p class="mt-3 text-danger-fg" role="alert">{{ pageError() }}</p>
          <a routerLink="/publish" class="btn btn-secondary mt-6">Back to studio</a>
        </div>
      } @else if (game(); as item) {
        <div class="mt-8 flex flex-wrap items-start justify-between gap-5">
          <div>
            <p class="text-accent-fg text-xs font-semibold uppercase tracking-[0.22em]">
              {{ workspace()?.name }} · Creator studio
            </p>
            <h1 class="font-display mt-3 text-4xl font-semibold text-fg">{{ item.title }}</h1>
            <p class="mt-2 text-sm text-fg-3">
              /{{ item.slug }} · {{ item.isPublished ? 'Published' : 'Offline' }}
            </p>
          </div>
          @if (item.activeReleaseId) {
            <div class="flex flex-wrap gap-2">
              <a
                class="btn btn-secondary"
                [routerLink]="['/games', item.id, 'play']"
                [queryParams]="{ preview: true }"
              >
                Open private preview
              </a>
              <button type="button" class="btn btn-secondary" (click)="copyPreviewLink()">
                Copy preview link
              </button>
            </div>
          }
        </div>
        @if (notice()) {
          <p
            class="mt-6 rounded-lg border border-line bg-surface px-4 py-3 text-sm text-success-fg"
            role="status"
          >
            {{ notice() }}
          </p>
        }
        @if (!workspace()?.canPublish) {
          <div class="card mt-8 p-6">
            <p class="text-fg-2">
              This workspace cannot publish right now. Its games stay offline until publishing
              access returns.
            </p>
            @if (config.auth.accountUrl) {
              <a class="btn btn-primary mt-4" [href]="config.auth.accountUrl">Manage plan</a>
            }
          </div>
        }
        <div class="mt-10 grid gap-8 lg:grid-cols-[minmax(0,1fr)_minmax(18rem,0.65fr)]">
          <div class="space-y-8">
            <form class="card p-7" (ngSubmit)="saveDetails()" #detailsForm="ngForm">
              <h2 class="font-display text-2xl font-semibold text-fg">Game details and access</h2>
              <p class="mt-2 text-sm text-fg-2">
                Changing public details or audience takes a public game offline until its current
                release is reviewed again.
              </p>
              <label for="title" class="mt-6 block text-sm font-medium text-fg">Title</label>
              <input
                id="title"
                name="title"
                required
                maxlength="200"
                [(ngModel)]="title"
                class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
              />
              <label for="description" class="mt-5 block text-sm font-medium text-fg"
                >Description</label
              >
              <textarea
                id="description"
                name="description"
                rows="5"
                maxlength="2000"
                [(ngModel)]="description"
                class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
              ></textarea>
              <label for="audience" class="mt-5 block text-sm font-medium text-fg"
                >Who can play</label
              >
              <select
                id="audience"
                name="audience"
                [(ngModel)]="audience"
                class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
              >
                <option value="Private">Private draft — publishers only</option>
                <option value="People">Specific people — invited players</option>
                <option value="Team">Team — workspace members</option>
                <option value="Public">Public — everyone after review</option>
              </select>
              @if (audience === 'Team' && workspace()?.kind !== 'Team') {
                <p class="mt-2 text-sm text-danger-fg">Team access requires a Team workspace.</p>
              }
              @if (detailsError()) {
                <p class="mt-4 text-sm text-danger-fg" role="alert">{{ detailsError() }}</p>
              }
              <button
                type="submit"
                class="btn btn-primary mt-6"
                [disabled]="
                  detailsForm.invalid ||
                  savingDetails() ||
                  !workspace()?.canPublish ||
                  (audience === 'Team' && workspace()?.kind !== 'Team')
                "
              >
                {{ savingDetails() ? 'Saving…' : 'Save details' }}
              </button>
            </form>

            <div class="card p-7">
              <h2 class="font-display text-2xl font-semibold text-fg">Browser build</h2>
              <p class="mt-2 text-sm text-fg-2">
                Export your Godot game for Web, then ZIP the export with <code>index.html</code> at
                the archive root. Packages are checked before they become releases.
              </p>
              <label for="game-zip" class="mt-6 block text-sm font-medium text-fg"
                >Godot Web ZIP</label
              >
              <input
                id="game-zip"
                type="file"
                accept=".zip,application/zip"
                (change)="chooseFile($event)"
                class="mt-2 block w-full text-sm text-fg-2 file:mr-4 file:rounded-lg file:border-0 file:bg-surface-2 file:px-4 file:py-2 file:text-fg"
              />
              @if (selectedFile(); as file) {
                <p class="mt-2 text-xs text-fg-3">
                  {{ file.name }} · {{ (file.size / 1048576).toFixed(1) }} MiB
                </p>
              }
              @if (uploading()) {
                <div class="mt-5">
                  <div class="flex justify-between text-sm text-fg-2">
                    <span>Uploading</span><span>{{ uploadProgress() }}%</span>
                  </div>
                  <progress
                    class="mt-2 w-full accent-[var(--color-accent)]"
                    max="100"
                    [value]="uploadProgress()"
                  ></progress>
                </div>
              }
              @if (upload(); as currentUpload) {
                <p class="mt-4 text-sm text-fg-2" role="status">
                  Package {{ currentUpload.status.toLowerCase()
                  }}{{
                    currentUpload.status === 'Failed' && currentUpload.errorCode
                      ? ': ' + currentUpload.errorCode
                      : ''
                  }}.
                </p>
                @if (
                  (currentUpload.status === 'Pending' || currentUpload.status === 'Processing') &&
                  !processing()
                ) {
                  <button
                    type="button"
                    class="mt-2 text-sm font-medium text-accent-fg"
                    (click)="resumeUpload()"
                  >
                    Check package status
                  </button>
                }
              }
              @if (uploadError()) {
                <p class="mt-4 text-sm text-danger-fg" role="alert">{{ uploadError() }}</p>
              }
              <button
                type="button"
                class="btn btn-primary mt-5"
                (click)="uploadBuild()"
                [disabled]="
                  !selectedFile() || uploading() || processing() || !workspace()?.canPublish
                "
              >
                {{
                  uploading() ? 'Uploading…' : processing() ? 'Checking package…' : 'Upload build'
                }}
              </button>
            </div>

            @if (item.audience === 'People') {
              <div class="card p-7">
                <h2 class="font-display text-2xl font-semibold text-fg">People with access</h2>
                <p class="mt-2 text-sm text-fg-2">
                  Invited players sign in with their own free account. Access starts when this game
                  is published.
                </p>
                <form
                  class="mt-6 grid gap-3 sm:grid-cols-[1fr_auto]"
                  (ngSubmit)="addPerson()"
                  #personForm="ngForm"
                >
                  <div>
                    <label for="person" class="block text-sm font-medium text-fg">{{
                      auth.mode === 'local' ? 'Local player subject' : 'Player email'
                    }}</label>
                    <input
                      id="person"
                      name="person"
                      required
                      [(ngModel)]="personInput"
                      [type]="auth.mode === 'local' ? 'text' : 'email'"
                      maxlength="254"
                      class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                      [placeholder]="
                        auth.mode === 'local' ? 'player-subject' : 'player@example.com'
                      "
                    />
                  </div>
                  <button
                    type="submit"
                    class="btn btn-secondary self-end"
                    [disabled]="personForm.invalid || sharing() || !workspace()?.canPublish"
                  >
                    {{ sharing() ? 'Adding…' : 'Add player' }}
                  </button>
                </form>
                <label for="share-expiry" class="mt-4 block text-sm font-medium text-fg"
                  >Expires (optional)</label
                >
                <input
                  id="share-expiry"
                  type="date"
                  [(ngModel)]="personExpiry"
                  name="personExpiry"
                  class="mt-2 rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                />
                @if (peopleError()) {
                  <p class="mt-4 text-sm text-danger-fg" role="alert">{{ peopleError() }}</p>
                }
                @if (people().length === 0) {
                  <p class="mt-5 text-sm text-fg-3">No players added yet.</p>
                } @else {
                  <ul class="mt-5 divide-y divide-line">
                    @for (person of people(); track person.subject) {
                      <li class="flex flex-wrap items-center justify-between gap-3 py-3">
                        <span class="min-w-0 break-all text-sm text-fg">{{
                          person.email || person.subject
                        }}</span>
                        <button
                          type="button"
                          class="text-sm font-medium text-danger-fg"
                          (click)="removePerson(person)"
                          [disabled]="sharing() || !workspace()?.canPublish"
                        >
                          Remove
                        </button>
                      </li>
                    }
                  </ul>
                }
              </div>
            }
          </div>

          <div class="space-y-8">
            <div class="card p-7">
              <div class="flex items-center justify-between gap-3">
                <h2 class="font-display text-2xl font-semibold text-fg">Releases</h2>
                <button
                  type="button"
                  class="text-sm font-medium text-accent-fg"
                  (click)="refreshReleaseState()"
                  [disabled]="releasesLoading()"
                >
                  Refresh status
                </button>
              </div>
              @if (releasesLoading()) {
                <p class="mt-4 text-sm text-fg-2">Loading releases…</p>
              } @else if (releasesError()) {
                <p class="mt-4 text-sm text-danger-fg" role="alert">{{ releasesError() }}</p>
                <button type="button" class="btn btn-secondary mt-3" (click)="reloadReleases()">
                  Try again
                </button>
              } @else if (releases().length === 0) {
                <p class="mt-4 text-sm text-fg-2">
                  Upload your first validated build to create a release.
                </p>
              } @else {
                <div class="mt-5 space-y-3">
                  @for (release of releases(); track release.id) {
                    <div class="rounded-lg border border-line p-4">
                      <label class="block cursor-pointer">
                        <span class="flex items-start gap-3">
                          <input
                            type="radio"
                            name="selectedRelease"
                            [value]="release.id"
                            [checked]="selectedReleaseId() === release.id"
                            (change)="selectedReleaseId.set(release.id)"
                          />
                          <span class="min-w-0 text-sm text-fg">
                            <strong>{{ release.createdAt | date: 'mediumDate' }}</strong>
                            @if (item.activeReleaseId === release.id) {
                              <span class="text-success-fg"> · Active</span>
                            }
                            <span class="mt-1 block break-all font-mono text-xs text-fg-3"
                              >{{ release.sha256.slice(0, 16) }}…</span
                            >
                            @if (item.audience === 'Public') {
                              <span class="mt-1 block text-xs text-fg-2"
                                >Review: {{ release.reviewStatus }}</span
                              >
                              @if (release.reviewReason) {
                                <span class="mt-1 block text-xs text-danger-fg">{{
                                  release.reviewReason
                                }}</span>
                              }
                            }
                          </span>
                        </span>
                      </label>
                      @if (item.activeReleaseId !== release.id) {
                        @if (confirmDeleteReleaseId() === release.id) {
                          <p class="mt-3 text-xs text-danger-fg">
                            Remove this release and its stored files?
                          </p>
                          <div class="mt-2 flex gap-3 text-sm">
                            <button
                              type="button"
                              class="text-fg-2"
                              (click)="confirmDeleteReleaseId.set(null)"
                            >
                              Cancel
                            </button>
                            <button
                              type="button"
                              class="font-medium text-danger-fg"
                              (click)="deleteRelease(release)"
                              [disabled]="deletingReleaseId() !== null || releaseActionBusy()"
                            >
                              {{
                                deletingReleaseId() === release.id ? 'Removing…' : 'Remove release'
                              }}
                            </button>
                          </div>
                        } @else {
                          <button
                            type="button"
                            class="mt-3 text-xs font-medium text-danger-fg"
                            (click)="confirmDeleteReleaseId.set(release.id)"
                          >
                            Remove release
                          </button>
                        }
                      }
                    </div>
                  }
                </div>
                @if (
                  item.audience === 'Public' &&
                  selectedReleaseId() &&
                  selectedReviewStatus() === 'NotRequested'
                ) {
                  <button
                    type="button"
                    class="btn btn-secondary mt-5"
                    (click)="requestReview()"
                    [disabled]="releaseActionBusy() || !workspace()?.canPublish"
                  >
                    Request public review
                  </button>
                }
                @if (item.audience === 'Public' && selectedReviewStatus() === 'Rejected') {
                  <p class="mt-4 text-sm text-fg-2">
                    To request review again, update the game details or upload a new release.
                  </p>
                }
                <button
                  type="button"
                  class="btn btn-primary mt-3"
                  (click)="activateRelease()"
                  [disabled]="
                    releaseActionBusy() ||
                    !selectedReleaseId() ||
                    item.activeReleaseId === selectedReleaseId() ||
                    (item.audience === 'Public' && selectedReviewStatus() !== 'Approved') ||
                    !workspace()?.canPublish
                  "
                >
                  {{ releaseActionBusy() ? 'Working…' : 'Activate selected release' }}
                </button>
              }
              @if (releaseActionError()) {
                <p class="mt-4 text-sm text-danger-fg" role="alert">{{ releaseActionError() }}</p>
              }
            </div>

            <div class="card p-7">
              <h2 class="font-display text-2xl font-semibold text-fg">Publication</h2>
              <p class="mt-2 text-sm text-fg-2">
                @if (item.isPublished) {
                  This game is live for its chosen audience.
                } @else if (item.audience === 'Public' && !item.publicReviewApproved) {
                  A public release needs review before it can go live.
                } @else {
                  This game is offline. Your active release can still be privately previewed by
                  publishers.
                }
              </p>
              @if (item.isModerationSuspended) {
                <p class="mt-3 text-sm text-danger-fg">This game is suspended by the operator.</p>
              }
              <button
                type="button"
                class="btn mt-5"
                [class.btn-primary]="!item.isPublished"
                [class.btn-secondary]="item.isPublished"
                (click)="togglePublication()"
                [disabled]="
                  publishing() ||
                  !workspace()?.canPublish ||
                  (!item.isPublished &&
                    (item.audience === 'Private' ||
                      !item.activeReleaseId ||
                      item.isModerationSuspended ||
                      (item.audience === 'Public' && !item.publicReviewApproved)))
                "
              >
                {{
                  publishing()
                    ? 'Updating…'
                    : item.isPublished
                      ? 'Take game offline'
                      : 'Publish game'
                }}
              </button>
              @if (publicationError()) {
                <p class="mt-4 text-sm text-danger-fg" role="alert">{{ publicationError() }}</p>
              }
            </div>
          </div>
        </div>
      }
    </section>
  `,
})
export class StudioGamePage implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly auth = inject(AuthService);
  protected readonly workspaces = inject(WorkspaceService);
  protected readonly config = inject(RUNTIME_CONFIG);
  private readonly workspaceId = this.route.snapshot.paramMap.get('workspaceId') ?? '';
  private readonly gameId = this.route.snapshot.paramMap.get('gameId') ?? '';
  private readonly base = `/api/workspaces/${encodeURIComponent(this.workspaceId)}/games/${encodeURIComponent(this.gameId)}`;
  private readonly pendingUploadKey = `starport.pendingUpload.${this.workspaceId}.${this.gameId}`;
  protected readonly game = signal<StudioGame | null>(null);
  protected readonly loading = signal(true);
  protected readonly pageError = signal('');
  protected readonly notice = signal('');
  protected readonly savingDetails = signal(false);
  protected readonly detailsError = signal('');
  protected readonly selectedFile = signal<File | null>(null);
  protected readonly uploading = signal(false);
  protected readonly uploadProgress = signal(0);
  protected readonly processing = signal(false);
  protected readonly upload = signal<GameUpload | null>(null);
  protected readonly uploadError = signal('');
  protected readonly releases = signal<GameRelease[]>([]);
  protected readonly releasesLoading = signal(false);
  protected readonly releasesError = signal('');
  protected readonly selectedReleaseId = signal<string | null>(null);
  protected readonly releaseActionBusy = signal(false);
  protected readonly releaseActionError = signal('');
  protected readonly confirmDeleteReleaseId = signal<string | null>(null);
  protected readonly deletingReleaseId = signal<string | null>(null);
  protected readonly publishing = signal(false);
  protected readonly publicationError = signal('');
  protected readonly people = signal<PersonShare[]>([]);
  protected readonly peopleError = signal('');
  protected readonly sharing = signal(false);
  protected title = '';
  protected description = '';
  protected audience: Audience = 'Private';
  protected personInput = '';
  protected personExpiry = '';
  private destroyed = false;

  protected workspace() {
    return this.workspaces.workspaces().find((workspace) => workspace.id === this.workspaceId);
  }

  protected selectedReviewStatus(): string {
    const status = this.releases().find(
      (release) => release.id === this.selectedReleaseId()
    )?.reviewStatus;
    return status ?? 'NotRequested';
  }

  ngOnInit(): void {
    void this.load();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
  }

  private async load(): Promise<void> {
    if (!this.workspaceId || !this.gameId) {
      this.pageError.set('This game link is invalid.');
      this.loading.set(false);
      return;
    }
    await this.auth.init();
    if (!this.auth.isAuthenticated()) {
      this.pageError.set('Sign in to manage this game.');
      this.loading.set(false);
      return;
    }
    await this.workspaces.ensureLoaded();
    if (!this.workspace() || this.workspace()?.role === 'Viewer') {
      this.pageError.set('You do not have access to this creator workspace.');
      this.loading.set(false);
      return;
    }
    this.workspaces.select(this.workspaceId);
    try {
      const game = await firstValueFrom(this.http.get<StudioGame>(this.base));
      if (this.destroyed) return;
      this.acceptGame(game);
      void this.loadReleases();
      void this.loadPeople();
      try {
        const pendingId = this.document.defaultView?.sessionStorage.getItem(this.pendingUploadKey);
        if (pendingId && /^[0-9a-f-]{36}$/i.test(pendingId)) void this.pollUpload(pendingId);
      } catch {
        // Storage is a convenience; releases are always read from the API.
      }
    } catch (error) {
      this.pageError.set(apiError(error, 'This game could not be loaded.'));
    } finally {
      this.loading.set(false);
    }
  }

  private acceptGame(game: StudioGame): void {
    this.game.set(game);
    this.title = game.title;
    this.description = game.description;
    this.audience = game.audience;
  }

  protected async saveDetails(): Promise<void> {
    if (!this.workspace()?.canPublish || this.savingDetails()) return;
    this.savingDetails.set(true);
    this.detailsError.set('');
    this.notice.set('');
    try {
      const game = await firstValueFrom(
        this.http.put<StudioGame>(this.base, {
          title: this.title.trim(),
          description: this.description.trim(),
          audience: this.audience,
        })
      );
      this.acceptGame(game);
      this.notice.set('Game details saved.');
      await Promise.all([this.loadReleases(), this.loadPeople()]);
    } catch (error) {
      this.detailsError.set(apiError(error, 'Game details could not be saved.'));
    } finally {
      this.savingDetails.set(false);
    }
  }

  protected chooseFile(event: Event): void {
    const file = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.uploadError.set('');
    if (
      file &&
      (!file.name.toLowerCase().endsWith('.zip') ||
        file.size === 0 ||
        file.size > MAX_ARCHIVE_BYTES)
    ) {
      this.selectedFile.set(null);
      this.uploadError.set(
        'Choose a nonempty ZIP under 256 MiB. Your workspace may have a lower limit.'
      );
      return;
    }
    this.selectedFile.set(file);
  }

  protected async uploadBuild(): Promise<void> {
    const file = this.selectedFile();
    if (!file || !this.workspace()?.canPublish || this.uploading() || this.processing()) return;
    this.uploading.set(true);
    this.uploadProgress.set(0);
    this.uploadError.set('');
    this.notice.set('');
    try {
      const response = await firstValueFrom(
        this.http
          .post<GameUpload>(`${this.base}/uploads`, file, {
            headers: { 'Content-Type': 'application/zip' },
            observe: 'events',
            reportProgress: true,
          })
          .pipe(
            tap((event) => {
              if (event.type === HttpEventType.UploadProgress && event.total)
                this.uploadProgress.set(Math.round((100 * event.loaded) / event.total));
            }),
            filter(
              (event): event is HttpResponse<GameUpload> => event.type === HttpEventType.Response
            ),
            takeUntilDestroyed(this.destroyRef)
          )
      );
      if (this.destroyed) return;
      const upload = response.body;
      if (!upload) throw new Error('Empty upload response');
      this.uploadProgress.set(100);
      this.upload.set(upload);
      this.selectedFile.set(null);
      this.uploading.set(false);
      try {
        this.document.defaultView?.sessionStorage.setItem(this.pendingUploadKey, upload.id);
      } catch {
        /* Optional resume state. */
      }
      await this.pollUpload(upload.id);
    } catch (error) {
      if (!this.destroyed)
        this.uploadError.set(apiError(error, 'The build could not be uploaded. Please try again.'));
    } finally {
      this.uploading.set(false);
    }
  }

  private async pollUpload(uploadId: string): Promise<void> {
    this.processing.set(true);
    const deadline = Date.now() + 10 * 60 * 1000;
    try {
      while (!this.destroyed && Date.now() < deadline) {
        await new Promise((resolve) => setTimeout(resolve, 2000));
        if (this.destroyed) return;
        const upload = await firstValueFrom(
          this.http.get<GameUpload>(`${this.base}/uploads/${encodeURIComponent(uploadId)}`)
        );
        this.upload.set(upload);
        if (upload.status === 'Ready') {
          this.forgetPendingUpload();
          this.notice.set('Build validated. Select the new release below to activate it.');
          await this.loadReleases(upload.releaseId);
          return;
        }
        if (upload.status === 'Failed') {
          this.forgetPendingUpload();
          this.uploadError.set(
            `The build did not pass validation${upload.errorCode ? ` (${upload.errorCode})` : ''}. Check the Godot Web export and upload another ZIP.`
          );
          return;
        }
      }
      if (!this.destroyed)
        this.uploadError.set(
          'Package validation is taking longer than expected. Check its status later.'
        );
    } catch (error) {
      this.uploadError.set(
        apiError(error, 'Could not check package status. Reload to see your releases.')
      );
    } finally {
      this.processing.set(false);
    }
  }

  protected resumeUpload(): void {
    const upload = this.upload();
    if (upload && !this.processing()) void this.pollUpload(upload.id);
  }

  private forgetPendingUpload(): void {
    try {
      this.document.defaultView?.sessionStorage.removeItem(this.pendingUploadKey);
    } catch {
      /* Optional resume state. */
    }
  }

  protected reloadReleases(): void {
    void this.loadReleases();
  }

  protected async refreshReleaseState(): Promise<void> {
    try {
      const game = await firstValueFrom(this.http.get<StudioGame>(this.base));
      if (!this.destroyed) this.game.set(game);
      await this.loadReleases();
    } catch (error) {
      this.releasesError.set(apiError(error, 'Review status could not be refreshed.'));
    }
  }

  private async loadReleases(selectId?: string | null): Promise<void> {
    this.releasesLoading.set(true);
    this.releasesError.set('');
    try {
      const releases = await firstValueFrom(this.http.get<GameRelease[]>(`${this.base}/releases`));
      if (this.destroyed) return;
      this.releases.set(releases);
      const selected = selectId ?? this.selectedReleaseId();
      this.selectedReleaseId.set(
        releases.find((release) => release.id === selected)?.id ??
          this.game()?.activeReleaseId ??
          releases[0]?.id ??
          null
      );
    } catch (error) {
      this.releasesError.set(apiError(error, 'Releases could not be loaded.'));
    } finally {
      this.releasesLoading.set(false);
    }
  }

  protected async requestReview(): Promise<void> {
    const releaseId = this.selectedReleaseId();
    if (!releaseId || !this.workspace()?.canPublish || this.releaseActionBusy()) return;
    this.releaseActionBusy.set(true);
    this.releaseActionError.set('');
    this.notice.set('');
    try {
      await firstValueFrom(this.http.post(`${this.base}/review-requests`, { releaseId }));
      await this.loadReleases(releaseId);
      this.notice.set('Public review requested for this release and the current game details.');
    } catch (error) {
      this.releaseActionError.set(apiError(error, 'Public review could not be requested.'));
    } finally {
      this.releaseActionBusy.set(false);
    }
  }

  protected async activateRelease(): Promise<void> {
    const releaseId = this.selectedReleaseId();
    if (!releaseId || !this.workspace()?.canPublish || this.releaseActionBusy()) return;
    this.releaseActionBusy.set(true);
    this.releaseActionError.set('');
    this.notice.set('');
    try {
      await firstValueFrom(this.http.put(`${this.base}/active-release`, { releaseId }));
      const game = await firstValueFrom(this.http.get<StudioGame>(this.base));
      this.acceptGame(game);
      await this.loadReleases(releaseId);
      this.notice.set('Release activated. Publish the game when you are ready for players.');
    } catch (error) {
      this.releaseActionError.set(apiError(error, 'The release could not be activated.'));
    } finally {
      this.releaseActionBusy.set(false);
    }
  }

  protected async deleteRelease(release: GameRelease): Promise<void> {
    if (
      this.game()?.activeReleaseId === release.id ||
      this.confirmDeleteReleaseId() !== release.id ||
      this.deletingReleaseId()
    )
      return;
    this.deletingReleaseId.set(release.id);
    this.releaseActionError.set('');
    try {
      await firstValueFrom(
        this.http.delete(`${this.base}/releases/${encodeURIComponent(release.id)}`)
      );
      this.releases.update((releases) => releases.filter((entry) => entry.id !== release.id));
      if (this.selectedReleaseId() === release.id)
        this.selectedReleaseId.set(this.game()?.activeReleaseId ?? this.releases()[0]?.id ?? null);
      this.confirmDeleteReleaseId.set(null);
      this.notice.set('Release removal queued. Stored usage will update after cleanup.');
    } catch (error) {
      this.releaseActionError.set(
        apiError(error, 'The release could not be removed. Refresh and try again.')
      );
    } finally {
      this.deletingReleaseId.set(null);
    }
  }

  protected async togglePublication(): Promise<void> {
    const game = this.game();
    if (!game || !this.workspace()?.canPublish || this.publishing()) return;
    this.publishing.set(true);
    this.publicationError.set('');
    this.notice.set('');
    try {
      const updated = await firstValueFrom(
        this.http.put<StudioGame>(`${this.base}/publication`, { published: !game.isPublished })
      );
      this.acceptGame(updated);
      this.notice.set(
        updated.isPublished ? 'Game published for its selected audience.' : 'Game taken offline.'
      );
    } catch (error) {
      this.publicationError.set(apiError(error, 'Publication could not be changed.'));
    } finally {
      this.publishing.set(false);
    }
  }

  private async loadPeople(): Promise<void> {
    if (this.game()?.audience !== 'People') return;
    this.peopleError.set('');
    try {
      this.people.set(await firstValueFrom(this.http.get<PersonShare[]>(`${this.base}/people`)));
    } catch (error) {
      this.peopleError.set(apiError(error, 'People with access could not be loaded.'));
    }
  }

  protected async addPerson(): Promise<void> {
    const input = this.personInput.trim();
    if (!input || !this.workspace()?.canPublish || this.sharing()) return;
    this.sharing.set(true);
    this.peopleError.set('');
    try {
      const expiresAt = this.personExpiry
        ? new Date(`${this.personExpiry}T23:59:59`).toISOString()
        : null;
      const body =
        this.auth.mode === 'local' ? { subject: input, expiresAt } : { email: input, expiresAt };
      await firstValueFrom(this.http.put(`${this.base}/people`, body));
      this.personInput = '';
      this.personExpiry = '';
      await this.loadPeople();
    } catch (error) {
      this.peopleError.set(apiError(error, 'The player could not be added.'));
    } finally {
      this.sharing.set(false);
    }
  }

  protected async removePerson(person: PersonShare): Promise<void> {
    if (!this.workspace()?.canPublish || this.sharing()) return;
    this.sharing.set(true);
    this.peopleError.set('');
    try {
      await firstValueFrom(
        this.http.delete(`${this.base}/people/${encodeURIComponent(person.subject)}`)
      );
      this.people.update((people) => people.filter((entry) => entry.subject !== person.subject));
    } catch (error) {
      this.peopleError.set(apiError(error, 'The player could not be removed.'));
    } finally {
      this.sharing.set(false);
    }
  }

  protected async copyPreviewLink(): Promise<void> {
    const game = this.game();
    if (!game) return;
    const link = `${this.document.location.origin}/games/${encodeURIComponent(game.id)}/play?preview=true`;
    try {
      const clipboard = this.document.defaultView?.navigator.clipboard;
      if (!clipboard) throw new Error('Clipboard unavailable');
      await clipboard.writeText(link);
      this.notice.set(
        'Private preview link copied. Only publishers in this workspace can open it.'
      );
    } catch {
      this.notice.set(`Private preview: ${link}`);
    }
  }
}
