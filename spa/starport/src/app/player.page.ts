import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import {
  DOCUMENT,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  ViewChild,
  effect,
  inject,
  signal,
} from '@angular/core';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './core/auth.service';
import { LaunchResponse, PlayerBridge, prepareEmbedUrl } from './core/player-bridge';

export interface GameCard {
  id: string;
  slug: string;
  title: string;
  description: string;
}

const DELIVERY_QUOTA_MESSAGE =
  'This game has reached its publisher’s monthly delivery limit. Please try again after it resets.';

function isDeliveryQuotaError(error: unknown): boolean {
  return (
    error instanceof HttpErrorResponse &&
    error.status === 429 &&
    error.error?.code === 'delivery-quota-exceeded'
  );
}

@Component({
  selector: 'app-game-detail',
  imports: [FormsModule, RouterLink],
  template: `
    <section class="mx-auto max-w-4xl px-6 py-16">
      <a routerLink="/" class="text-sm text-fg-2 hover:text-fg">← All games</a>
      @if (loading()) {
        <p class="mt-10 text-fg-2">Loading game…</p>
      } @else if (error()) {
        <div class="card mt-10 p-8">
          <h1 class="font-display text-3xl font-semibold text-fg">Game unavailable</h1>
          <p class="mt-3 text-fg-2" role="alert">{{ error() }}</p>
          @if (!auth.isAuthenticated() && !quotaExceeded()) {
            <button type="button" class="btn btn-primary mt-6" (click)="signIn()">
              Sign in to check access
            </button>
          }
        </div>
      } @else if (game(); as item) {
        <p class="text-accent-fg mt-12 text-xs font-semibold uppercase tracking-[0.22em]">
          Free to play
        </p>
        <h1 class="font-display mt-4 text-5xl font-semibold tracking-tight text-fg md:text-7xl">
          {{ item.title }}
        </h1>
        <p class="mt-6 max-w-2xl text-lg text-fg-2">
          {{ item.description || 'A world to discover on Starport.' }}
        </p>
        <div class="mt-8 flex flex-wrap items-center gap-4">
          <a
            class="btn btn-primary"
            [routerLink]="['/games', item.id, 'play']"
            [queryParams]="preview ? { preview: true } : {}"
            >Play game</a
          >
          @if (!auth.isAuthenticated()) {
            <span class="text-sm text-fg-3">Sign in to save your progress across devices.</span>
          }
        </div>
        @if (!preview) {
          <div class="mt-16 border-t border-line pt-8">
            <h2 class="font-display text-xl font-semibold text-fg">Report a problem</h2>
            <p class="mt-2 text-sm text-fg-2">
              Tell the Starport operators about abuse, copyright concerns, malware, or another
              serious issue with this game.
            </p>
            @if (!auth.isAuthenticated()) {
              <button type="button" class="btn btn-secondary mt-5" (click)="signIn()">
                Sign in to report
              </button>
            } @else if (reportSent()) {
              <p class="mt-5 text-sm text-success-fg" role="status">
                Your report was sent to the operators.
              </p>
            } @else {
              <form class="mt-5 max-w-xl" (ngSubmit)="submitReport()" #reportForm="ngForm">
                <label for="report-category" class="block text-sm font-medium text-fg"
                  >Reason</label
                >
                <select
                  id="report-category"
                  name="category"
                  [(ngModel)]="reportCategory"
                  required
                  class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                >
                  <option value="abuse">Abuse or harmful content</option>
                  <option value="copyright">Copyright concern</option>
                  <option value="malware">Malware or unsafe behavior</option>
                  <option value="other">Other issue</option>
                </select>
                <label for="report-description" class="mt-4 block text-sm font-medium text-fg"
                  >What happened?</label
                >
                <textarea
                  id="report-description"
                  name="description"
                  rows="3"
                  required
                  maxlength="2000"
                  [(ngModel)]="reportDescription"
                  class="mt-2 w-full rounded-lg border border-line bg-surface px-3 py-2 text-fg"
                ></textarea>
                @if (reportError()) {
                  <p class="mt-3 text-sm text-danger-fg" role="alert">{{ reportError() }}</p>
                }
                <button
                  type="submit"
                  class="btn btn-secondary mt-4"
                  [disabled]="reportForm.invalid || reporting()"
                >
                  {{ reporting() ? 'Sending…' : 'Send report' }}
                </button>
              </form>
            }
          </div>
        }
      }
    </section>
  `,
})
export class GameDetailPage implements OnInit {
  private readonly http = inject(HttpClient);
  private readonly route = inject(ActivatedRoute);
  protected readonly auth = inject(AuthService);
  protected readonly game = signal<GameCard | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal('');
  protected readonly quotaExceeded = signal(false);
  protected readonly preview = this.route.snapshot.queryParamMap.get('preview') === 'true';
  protected readonly reporting = signal(false);
  protected readonly reportSent = signal(false);
  protected readonly reportError = signal('');
  protected reportCategory = 'abuse';
  protected reportDescription = '';

  ngOnInit(): void {
    void this.load();
  }

  private async load(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('gameId');
    if (!id) {
      this.error.set('This game link is invalid.');
      this.loading.set(false);
      return;
    }
    try {
      this.game.set(
        await firstValueFrom(
          this.http.get<GameCard>(
            `/api/games/${encodeURIComponent(id)}${this.preview ? '?preview=true' : ''}`
          )
        )
      );
    } catch (error) {
      this.quotaExceeded.set(isDeliveryQuotaError(error));
      this.error.set(
        this.quotaExceeded()
          ? DELIVERY_QUOTA_MESSAGE
          : 'This game may be offline or shared with another account.'
      );
    } finally {
      this.loading.set(false);
    }
  }

  protected signIn(): void {
    void this.auth.login(
      `/games/${encodeURIComponent(this.route.snapshot.paramMap.get('gameId') ?? '')}${this.preview ? '?preview=true' : ''}`
    );
  }

  protected async submitReport(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('gameId');
    if (!id || !this.auth.isAuthenticated() || this.reporting()) return;
    this.reporting.set(true);
    this.reportError.set('');
    try {
      await firstValueFrom(
        this.http.post(`/api/games/${encodeURIComponent(id)}/reports`, {
          category: this.reportCategory,
          description: this.reportDescription.trim(),
        })
      );
      this.reportSent.set(true);
    } catch (error) {
      this.reportError.set(
        error instanceof HttpErrorResponse && error.status === 429
          ? 'Please wait before sending another report.'
          : 'Your report could not be sent. Please try again.'
      );
    } finally {
      this.reporting.set(false);
    }
  }
}

@Component({
  selector: 'app-play',
  imports: [RouterLink],
  template: `
    <section class="mx-auto max-w-7xl px-6 py-8">
      <div class="mb-5 flex flex-wrap items-center justify-between gap-4">
        <div>
          <a routerLink="/" class="text-sm text-fg-2 hover:text-fg">← All games</a>
          <h1 class="font-display mt-2 text-2xl font-semibold text-fg">
            {{ game()?.title || 'Loading game…' }}
          </h1>
        </div>
        @if (game(); as item) {
          <a
            class="text-sm text-fg-2 hover:text-fg"
            [routerLink]="['/games', item.id]"
            [queryParams]="preview ? { preview: true } : {}"
            >Game details</a
          >
        }
      </div>
      @if (loading()) {
        <p class="py-12 text-fg-2">Preparing your game…</p>
      }
      @if (error()) {
        <div class="card mb-6 p-8">
          <h2 class="font-display text-2xl font-semibold text-fg">Game could not start</h2>
          <p class="mt-3 text-fg-2" role="alert">{{ error() }}</p>
          @if (!auth.isAuthenticated() && !quotaExceeded()) {
            <button type="button" class="btn btn-primary mt-6" (click)="signIn()">
              Sign in to check access
            </button>
          }
        </div>
      }
      <iframe
        #gameFrame
        title="Starport game"
        class="aspect-video w-full rounded-xl border border-line bg-black"
        [class.hidden]="!ready()"
        [src]="frameSrc()"
        sandbox="allow-scripts allow-same-origin allow-pointer-lock"
        allow="fullscreen; gamepad; autoplay"
        allowfullscreen
        referrerpolicy="no-referrer"
      ></iframe>
      @if (ready() && !auth.isAuthenticated()) {
        <p class="mt-4 text-sm text-fg-3">You can play as a guest. Sign in to use cloud saves.</p>
      }
    </section>
  `,
})
export class PlayPage implements OnInit, OnDestroy {
  @ViewChild('gameFrame') private frame?: ElementRef<HTMLIFrameElement>;
  private readonly http = inject(HttpClient);
  private readonly route = inject(ActivatedRoute);
  protected readonly preview = this.route.snapshot.queryParamMap.get('preview') === 'true';
  private readonly document = inject(DOCUMENT);
  private readonly sanitizer = inject(DomSanitizer);
  protected readonly auth = inject(AuthService);
  protected readonly game = signal<GameCard | null>(null);
  protected readonly loading = signal(true);
  protected readonly ready = signal(false);
  protected readonly error = signal('');
  protected readonly quotaExceeded = signal(false);
  protected readonly frameSrc = signal<SafeResourceUrl>(
    this.sanitizer.bypassSecurityTrustResourceUrl('about:blank')
  );
  private bridge: PlayerBridge | null = null;
  private readonly launchedWithSaveAccess = signal(false);
  private destroyed = false;
  private readonly stopOnSignOut = effect(() => {
    if (this.launchedWithSaveAccess() && !this.auth.isAuthenticated()) {
      this.bridge?.dispose();
      this.bridge = null;
      this.frameSrc.set(this.sanitizer.bypassSecurityTrustResourceUrl('about:blank'));
      this.ready.set(false);
      this.error.set('Your sign-in session ended. Open the game again after signing in.');
    }
  });

  ngOnInit(): void {
    void this.launch();
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.bridge?.dispose();
  }

  private async launch(): Promise<void> {
    const id = this.route.snapshot.paramMap.get('gameId');
    if (!id) {
      this.error.set('This game link is invalid.');
      this.loading.set(false);
      return;
    }
    try {
      const game = await firstValueFrom(
        this.http.get<GameCard>(
          `/api/games/${encodeURIComponent(id)}${this.preview ? '?preview=true' : ''}`
        )
      );
      if (this.destroyed) return;
      this.game.set(game);
      const launch = await firstValueFrom(
        this.http.post<LaunchResponse>(
          `/api/games/${encodeURIComponent(id)}/launch${this.preview ? '?preview=true' : ''}`,
          {}
        )
      );
      if (this.destroyed) return;
      const url = prepareEmbedUrl(launch, this.document.location.origin);
      if (!this.frame?.nativeElement || !this.document.defaultView)
        throw new Error('The player frame is unavailable.');
      this.launchedWithSaveAccess.set(launch.canSave);
      this.bridge = new PlayerBridge(
        this.frame.nativeElement,
        this.document.defaultView,
        this.http,
        id,
        launch,
        () => this.auth.isAuthenticated(),
        this.preview
      );
      // Angular requires a trusted ResourceUrl for iframe src; the exact content origin was checked above.
      this.frameSrc.set(this.sanitizer.bypassSecurityTrustResourceUrl(url.href));
      this.ready.set(true);
    } catch (error) {
      console.error('[Player] Could not launch game', error);
      if (!this.destroyed) {
        this.quotaExceeded.set(isDeliveryQuotaError(error));
        this.error.set(
          this.quotaExceeded()
            ? DELIVERY_QUOTA_MESSAGE
            : 'This game may be offline or you may need access from its creator.'
        );
      }
    } finally {
      if (!this.destroyed) this.loading.set(false);
    }
  }

  protected signIn(): void {
    void this.auth.login(
      `/games/${encodeURIComponent(this.route.snapshot.paramMap.get('gameId') ?? '')}/play${this.preview ? '?preview=true' : ''}`
    );
  }
}
