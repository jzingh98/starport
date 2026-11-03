import { DatePipe, DOCUMENT } from '@angular/common';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './core/auth.service';
import { LaunchResponse, prepareEmbedUrl } from './core/player-bridge';

interface PendingReview {
  id: string;
  gameId: string;
  gameTitle: string;
  gameDescription?: string;
  gameSlug?: string;
  releaseId: string;
  releaseSha256: string;
  metadataRevision: number;
  requestedAt: string;
}

interface GameReport {
  id: string;
  gameId: string;
  gameTitle: string;
  category: string;
  description: string;
  reporterSubject: string;
  createdAt: string;
  isModerationSuspended: boolean;
}

@Component({
  selector: 'app-review',
  imports: [DatePipe, FormsModule, RouterLink],
  template: `
    <section class="mx-auto max-w-5xl px-6 py-16">
      <a routerLink="/" class="text-sm text-fg-2 hover:text-fg">← Games</a>
      <p class="text-accent-fg mt-8 text-xs font-semibold uppercase tracking-[0.22em]">Operator</p>
      <h1 class="font-display mt-4 text-5xl font-semibold text-fg">Public review queue</h1>
      <p class="mt-4 max-w-2xl text-fg-2">
        Review requests are bound to a specific release digest and revision of the game's details.
        Approving one candidate does not approve later changes.
      </p>
      @if (loading()) {
        <p class="mt-10 text-fg-2">Loading review requests…</p>
      } @else if (error()) {
        <div class="card mt-10 p-7">
          <p class="text-danger-fg" role="alert">{{ error() }}</p>
          <button type="button" class="btn btn-secondary mt-5" (click)="reload()">Try again</button>
        </div>
      } @else if (requests().length === 0) {
        <div class="card mt-10 p-8">
          <h2 class="font-display text-2xl font-semibold text-fg">Queue clear</h2>
          <p class="mt-2 text-fg-2">There are no public game requests waiting for review.</p>
        </div>
      } @else {
        <div class="mt-10 space-y-6">
          @for (request of requests(); track request.id) {
            <article class="card p-7">
              <div class="flex flex-wrap items-start justify-between gap-3">
                <div>
                  <h2 class="font-display text-2xl font-semibold text-fg">
                    {{ request.gameTitle }}
                  </h2>
                  <p class="mt-1 text-sm text-fg-3">
                    Requested {{ request.requestedAt | date: 'medium' }}
                  </p>
                </div>
                <span class="rounded-full border border-line px-3 py-1 text-xs text-fg-2"
                  >Pending</span
                >
              </div>
              @if (request.gameDescription) {
                <p class="mt-4 max-w-3xl text-fg-2">{{ request.gameDescription }}</p>
              }
              <dl class="mt-6 grid gap-4 text-sm sm:grid-cols-2">
                <div>
                  <dt class="text-fg-3">Game ID</dt>
                  <dd class="mt-1 break-all font-mono text-fg">{{ request.gameId }}</dd>
                </div>
                <div>
                  <dt class="text-fg-3">Metadata revision</dt>
                  <dd class="mt-1 text-fg">{{ request.metadataRevision }}</dd>
                </div>
                <div class="sm:col-span-2">
                  <dt class="text-fg-3">Release SHA-256</dt>
                  <dd class="mt-1 break-all font-mono text-fg">{{ request.releaseSha256 }}</dd>
                </div>
              </dl>
              <p class="mt-5 text-sm text-fg-2">
                Inspect the exact candidate build and its current details before making a decision.
              </p>
              <button
                type="button"
                class="btn btn-secondary mt-4"
                (click)="openPreview(request)"
                [disabled]="previewLoading()"
              >
                {{
                  previewLoading() && previewRequestId() === request.id
                    ? 'Opening preview…'
                    : 'Open candidate preview'
                }}
              </button>
              @if (previewError()[request.id]) {
                <p class="mt-3 text-sm text-danger-fg" role="alert">
                  {{ previewError()[request.id] }}
                </p>
              }
              @if (previewRequestId() === request.id && previewReady()) {
                <iframe
                  title="Candidate game preview"
                  class="mt-5 aspect-video w-full rounded-xl border border-line bg-black"
                  [src]="previewFrameSrc()"
                  sandbox="allow-scripts allow-same-origin allow-pointer-lock"
                  allow="fullscreen; gamepad; autoplay"
                  allowfullscreen
                  referrerpolicy="no-referrer"
                ></iframe>
                <p class="mt-2 text-xs text-fg-3">
                  Preview only. Cloud saves are disabled for reviewers.
                </p>
              }
              <label class="mt-6 block text-sm font-medium text-fg" [for]="'reason-' + request.id"
                >Rejection reason</label
              >
              <textarea
                [id]="'reason-' + request.id"
                [name]="'reason-' + request.id"
                rows="2"
                maxlength="500"
                [(ngModel)]="reasons[request.id]"
                placeholder="Required when rejecting; shared with the creator"
                class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
              ></textarea>
              @if (decisionError()[request.id]) {
                <p class="mt-3 text-sm text-danger-fg" role="alert">
                  {{ decisionError()[request.id] }}
                </p>
              }
              <div class="mt-5 flex flex-wrap gap-3">
                <button
                  type="button"
                  class="btn btn-primary"
                  (click)="decide(request, true)"
                  [disabled]="busyId() !== null || !previewedIds().has(request.id)"
                >
                  Approve
                </button>
                <button
                  type="button"
                  class="btn btn-secondary"
                  (click)="decide(request, false)"
                  [disabled]="busyId() !== null || !reasons[request.id]?.trim()"
                >
                  Reject
                </button>
              </div>
            </article>
          }
        </div>
      }

      <div class="mt-16 border-t border-line pt-10">
        <h2 class="font-display text-3xl font-semibold text-fg">Player reports</h2>
        <p class="mt-2 text-fg-2">
          Review reported games, record a resolution, or suspend a game immediately.
        </p>
        @if (reportsLoading()) {
          <p class="mt-6 text-fg-2">Loading reports…</p>
        } @else if (reportsError()) {
          <p class="mt-6 text-danger-fg" role="alert">{{ reportsError() }}</p>
          <button type="button" class="btn btn-secondary mt-3" (click)="reloadReports()">
            Try again
          </button>
        } @else if (reports().length === 0) {
          <p class="card mt-6 p-6 text-fg-2">No open player reports.</p>
        } @else {
          <div class="mt-6 space-y-5">
            @for (report of reports(); track report.id) {
              <article class="card p-7">
                <div class="flex flex-wrap items-start justify-between gap-3">
                  <div>
                    <h3 class="font-display text-2xl font-semibold text-fg">
                      {{ report.gameTitle }}
                    </h3>
                    <p class="mt-1 text-sm text-fg-3">
                      {{ report.category }} · {{ report.createdAt | date: 'medium' }}
                    </p>
                  </div>
                  <span class="rounded-full border border-line px-3 py-1 text-xs text-fg-2"
                    >Open report</span
                  >
                </div>
                <p class="mt-5 whitespace-pre-wrap text-fg-2">{{ report.description }}</p>
                <p class="mt-3 break-all text-xs text-fg-3">
                  Game {{ report.gameId }} · Reporter {{ report.reporterSubject }}
                </p>
                <div class="mt-7 grid gap-6 lg:grid-cols-2">
                  <div>
                    <label
                      class="block text-sm font-medium text-fg"
                      [for]="'resolution-' + report.id"
                      >Resolution note</label
                    >
                    <textarea
                      [id]="'resolution-' + report.id"
                      [name]="'resolution-' + report.id"
                      rows="3"
                      maxlength="1000"
                      [(ngModel)]="resolutions[report.id]"
                      placeholder="What was checked and how was this resolved?"
                      class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                    ></textarea>
                    <button
                      type="button"
                      class="btn btn-secondary mt-3"
                      (click)="resolveReport(report)"
                      [disabled]="reportBusyId() !== null || !resolutions[report.id]?.trim()"
                    >
                      Resolve report
                    </button>
                  </div>
                  <div>
                    <label
                      class="block text-sm font-medium text-fg"
                      [for]="'suspension-' + report.id"
                      >Moderation reason</label
                    >
                    <textarea
                      [id]="'suspension-' + report.id"
                      [name]="'suspension-' + report.id"
                      rows="3"
                      maxlength="1000"
                      [(ngModel)]="suspensionReasons[report.id]"
                      placeholder="Required for suspension or reinstatement"
                      class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                    ></textarea>
                    <div class="mt-3 flex flex-wrap gap-2">
                      @if (!report.isModerationSuspended) {
                        <button
                          type="button"
                          class="btn btn-secondary"
                          (click)="setSuspension(report, true)"
                          [disabled]="
                            reportBusyId() !== null || !suspensionReasons[report.id]?.trim()
                          "
                        >
                          Suspend game
                        </button>
                      } @else {
                        <button
                          type="button"
                          class="btn btn-secondary"
                          (click)="setSuspension(report, false)"
                          [disabled]="
                            reportBusyId() !== null || !suspensionReasons[report.id]?.trim()
                          "
                        >
                          Lift suspension
                        </button>
                      }
                    </div>
                  </div>
                </div>
                @if (reportActionNotice()[report.id]) {
                  <p class="mt-4 text-sm text-success-fg" role="status">
                    {{ reportActionNotice()[report.id] }}
                  </p>
                }
                @if (reportActionError()[report.id]) {
                  <p class="mt-4 text-sm text-danger-fg" role="alert">
                    {{ reportActionError()[report.id] }}
                  </p>
                }
              </article>
            }
          </div>
        }
      </div>
    </section>
  `,
})
export class ReviewPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly document = inject(DOCUMENT);
  private readonly sanitizer = inject(DomSanitizer);
  protected readonly auth = inject(AuthService);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly requests = signal<PendingReview[]>([]);
  protected readonly busyId = signal<string | null>(null);
  protected readonly decisionError = signal<Record<string, string>>({});
  protected readonly previewRequestId = signal<string | null>(null);
  protected readonly previewLoading = signal(false);
  protected readonly previewReady = signal(false);
  protected readonly previewedIds = signal<Set<string>>(new Set());
  protected readonly previewError = signal<Record<string, string>>({});
  protected readonly previewFrameSrc = signal<SafeResourceUrl>(
    this.sanitizer.bypassSecurityTrustResourceUrl('about:blank')
  );
  protected reasons: Record<string, string> = {};
  protected readonly reportsLoading = signal(true);
  protected readonly reportsError = signal('');
  protected readonly reports = signal<GameReport[]>([]);
  protected readonly reportBusyId = signal<string | null>(null);
  protected readonly reportActionNotice = signal<Record<string, string>>({});
  protected readonly reportActionError = signal<Record<string, string>>({});
  protected resolutions: Record<string, string> = {};
  protected suspensionReasons: Record<string, string> = {};

  ngOnInit(): void {
    void this.load();
  }

  protected reload(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    this.loading.set(true);
    this.error.set('');
    await this.auth.init();
    if (!this.auth.isAuthenticated()) {
      this.error.set('Sign in with an operator account to review public games.');
      this.loading.set(false);
      this.reportsError.set('Sign in with an operator account to review reports.');
      this.reportsLoading.set(false);
      return;
    }
    void this.loadReports();
    try {
      this.requests.set(
        await firstValueFrom(this.http.get<PendingReview[]>('/api/admin/review-requests'))
      );
      this.previewedIds.set(new Set());
      this.previewReady.set(false);
      this.previewRequestId.set(null);
      this.previewFrameSrc.set(this.sanitizer.bypassSecurityTrustResourceUrl('about:blank'));
    } catch (error) {
      this.error.set(
        error instanceof HttpErrorResponse && error.status === 404
          ? 'This account cannot access the review queue.'
          : 'Review requests could not be loaded. Please try again.'
      );
    } finally {
      this.loading.set(false);
    }
  }

  protected reloadReports(): void {
    void this.loadReports();
  }

  private async loadReports(): Promise<void> {
    this.reportsLoading.set(true);
    this.reportsError.set('');
    try {
      this.reports.set(await firstValueFrom(this.http.get<GameReport[]>('/api/admin/reports')));
    } catch (error) {
      this.reportsError.set(
        error instanceof HttpErrorResponse && error.status === 404
          ? 'This account cannot access player reports.'
          : 'Player reports could not be loaded. Please try again.'
      );
    } finally {
      this.reportsLoading.set(false);
    }
  }

  protected async resolveReport(report: GameReport): Promise<void> {
    const resolution = this.resolutions[report.id]?.trim();
    if (!resolution || this.reportBusyId()) return;
    this.reportBusyId.set(report.id);
    this.reportActionError.update((errors) => ({ ...errors, [report.id]: '' }));
    try {
      await firstValueFrom(
        this.http.post(`/api/admin/reports/${encodeURIComponent(report.id)}/resolve`, {
          resolution,
        })
      );
      this.reports.update((reports) => reports.filter((entry) => entry.id !== report.id));
    } catch {
      this.reportActionError.update((errors) => ({
        ...errors,
        [report.id]: 'The report could not be resolved. Refresh and try again.',
      }));
    } finally {
      this.reportBusyId.set(null);
    }
  }

  protected async setSuspension(report: GameReport, suspended: boolean): Promise<void> {
    const reason = this.suspensionReasons[report.id]?.trim();
    if (!reason || this.reportBusyId()) return;
    this.reportBusyId.set(report.id);
    this.reportActionNotice.update((messages) => ({ ...messages, [report.id]: '' }));
    this.reportActionError.update((errors) => ({ ...errors, [report.id]: '' }));
    try {
      await firstValueFrom(
        this.http.put(`/api/admin/games/${encodeURIComponent(report.gameId)}/suspension`, {
          suspended,
          reason,
        })
      );
      this.reportActionNotice.update((messages) => ({
        ...messages,
        [report.id]: suspended
          ? 'Game suspended and taken offline. Resolve the report when your investigation is complete.'
          : 'Suspension lifted. The creator must publish the game again before players can access it.',
      }));
      this.reports.update((reports) =>
        reports.map((entry) =>
          entry.id === report.id ? { ...entry, isModerationSuspended: suspended } : entry
        )
      );
      this.suspensionReasons[report.id] = '';
    } catch {
      this.reportActionError.update((errors) => ({
        ...errors,
        [report.id]: 'The moderation action could not be saved. Refresh and try again.',
      }));
    } finally {
      this.reportBusyId.set(null);
    }
  }

  protected async openPreview(request: PendingReview): Promise<void> {
    if (this.previewLoading()) return;
    this.previewLoading.set(true);
    this.previewRequestId.set(request.id);
    this.previewReady.set(false);
    this.previewError.update((errors) => ({ ...errors, [request.id]: '' }));
    this.previewFrameSrc.set(this.sanitizer.bypassSecurityTrustResourceUrl('about:blank'));
    try {
      const launch = await firstValueFrom(
        this.http.post<LaunchResponse>(
          `/api/admin/review-requests/${encodeURIComponent(request.id)}/preview-launch`,
          {}
        )
      );
      if (launch.canSave) throw new Error('Moderator preview unexpectedly allows saves');
      const url = prepareEmbedUrl(launch, this.document.location.origin);
      this.previewFrameSrc.set(this.sanitizer.bypassSecurityTrustResourceUrl(url.href));
      this.previewedIds.update((ids) => new Set(ids).add(request.id));
      this.previewReady.set(true);
    } catch (error) {
      this.previewError.update((errors) => ({
        ...errors,
        [request.id]:
          error instanceof HttpErrorResponse && error.status === 409
            ? 'This candidate changed. Refresh the queue before reviewing.'
            : 'Candidate preview could not be opened.',
      }));
    } finally {
      this.previewLoading.set(false);
    }
  }

  protected async decide(request: PendingReview, approved: boolean): Promise<void> {
    if (this.busyId() !== null) return;
    if (approved && !this.previewedIds().has(request.id)) return;
    const reason = this.reasons[request.id]?.trim() || null;
    if (!approved && !reason) return;
    this.busyId.set(request.id);
    this.decisionError.update((errors) => ({ ...errors, [request.id]: '' }));
    try {
      await firstValueFrom(
        this.http.post(`/api/admin/review-requests/${encodeURIComponent(request.id)}/decision`, {
          approved,
          reason,
        })
      );
      this.requests.update((requests) => requests.filter((entry) => entry.id !== request.id));
    } catch (error) {
      this.decisionError.update((errors) => ({
        ...errors,
        [request.id]:
          error instanceof HttpErrorResponse && error.status === 409
            ? 'This request changed or the game was updated. Refresh the queue.'
            : 'The decision could not be saved. Please try again.',
      }));
    } finally {
      this.busyId.set(null);
    }
  }
}
