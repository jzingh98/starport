import { HttpInterceptorFn } from '@angular/common/http';
import { DOCUMENT, Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import type { User, UserManager } from 'oidc-client-ts';
import { from, switchMap } from 'rxjs';
import { RUNTIME_CONFIG } from './runtime-config';

const CALLBACK_PATH = '/auth/callback';
const RENEWAL_LOCK = 'starport-oidc-renewal';

function hasModeratorRole(claims: unknown): boolean {
  if (!claims || typeof claims !== 'object') return false;
  const access = (claims as { resource_access?: unknown }).resource_access;
  if (!access || typeof access !== 'object') return false;
  const starport = (access as { starport?: unknown }).starport;
  return (
    !!starport &&
    typeof starport === 'object' &&
    Array.isArray((starport as { roles?: unknown }).roles) &&
    (starport as { roles: unknown[] }).roles.includes('moderator')
  );
}

function tokenHasModeratorRole(token: string): boolean {
  try {
    const payload = token.split('.')[1];
    if (!payload) return false;
    const base64 = payload.replace(/-/g, '+').replace(/_/g, '/');
    return hasModeratorRole(JSON.parse(atob(base64.padEnd(Math.ceil(base64.length / 4) * 4, '='))));
  } catch {
    return false;
  }
}

export function safeReturnUrl(value: unknown): string {
  if (
    typeof value !== 'string' ||
    !value.startsWith('/') ||
    value.startsWith('//') ||
    value.startsWith('/\\')
  ) {
    return '/';
  }
  return value;
}

/** Browser OIDC session. Local mode sends no token and uses the server's development identity. */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly config = inject(RUNTIME_CONFIG);
  private readonly document = inject(DOCUMENT);
  private readonly router = inject(Router);
  private readonly oidcUser = signal<User | null>(null);
  private readonly initialized = signal(false);
  private manager: UserManager | null = null;
  private initialization: Promise<void> | null = null;
  private renewal: Promise<User | null> | null = null;

  readonly mode = this.config.auth.mode;
  readonly loading = computed(() => !this.initialized());
  readonly isAuthenticated = computed(() => this.mode === 'local' || this.oidcUser() !== null);
  /** Navigation hint only; the API makes the actual moderator decision. */
  readonly isModerator = computed(() => {
    if (this.mode === 'local') return true;
    const user = this.oidcUser();
    return !!user && (hasModeratorRole(user.profile) || tokenHasModeratorRole(user.access_token));
  });
  readonly name = computed(() =>
    this.mode === 'local'
      ? 'Local developer'
      : (this.oidcUser()?.profile.name ?? this.oidcUser()?.profile.preferred_username ?? 'Player')
  );

  init(): Promise<void> {
    return (this.initialization ??= this.initialize());
  }

  async login(returnUrl: string = this.router.url): Promise<void> {
    if (this.mode === 'local') return;
    const manager = await this.requireManager();
    await manager.signinRedirect({ state: { returnUrl: safeReturnUrl(returnUrl) } });
  }

  async completeSignIn(): Promise<string> {
    const manager = await this.requireManager();
    const user = await manager.signinRedirectCallback(this.document.location.href);
    this.oidcUser.set(user);
    return safeReturnUrl((user.state as { returnUrl?: unknown } | undefined)?.returnUrl);
  }

  async logout(): Promise<void> {
    if (this.mode === 'local') return;
    const manager = await this.requireManager();
    const endSession = await manager.metadataService.getEndSessionEndpoint().catch(() => undefined);
    if (endSession) {
      this.oidcUser.set(null);
      await manager.signoutRedirect();
      return;
    }
    await manager.removeUser();
    this.oidcUser.set(null);
    await this.router.navigateByUrl('/');
  }

  async token(): Promise<string | null> {
    if (this.mode === 'local') return null;
    await this.init();
    let user = this.oidcUser();
    if (user?.expired || (user?.expires_in ?? Number.POSITIVE_INFINITY) < 30) {
      user = await this.renew();
    }
    return user && !user.expired ? user.access_token : null;
  }

  private async initialize(): Promise<void> {
    try {
      if (this.mode !== 'oidc') return;
      const { UserManager, WebStorageStateStore } = await import('oidc-client-ts');
      const origin = this.document.location.origin;
      this.manager = new UserManager({
        authority: this.config.auth.authority!,
        client_id: this.config.auth.clientId!,
        redirect_uri: `${origin}${CALLBACK_PATH}`,
        post_logout_redirect_uri: `${origin}/`,
        response_type: 'code',
        scope: this.config.auth.scopes || 'openid profile email',
        automaticSilentRenew: false,
        userStore: new WebStorageStateStore({ store: this.document.defaultView!.localStorage }),
      });
      this.manager.events.addUserLoaded((user) => this.oidcUser.set(user));
      this.manager.events.addUserUnloaded(() => this.oidcUser.set(null));
      this.manager.events.addAccessTokenExpiring(() => void this.renew());
      this.oidcUser.set(await this.manager.getUser());
      if (this.oidcUser()?.expired) await this.renew();
      void this.manager.clearStaleState().catch(() => undefined);
    } catch (error) {
      console.error('[Auth] Could not restore the sign-in session', error);
      this.oidcUser.set(null);
    } finally {
      this.initialized.set(true);
    }
  }

  private async requireManager(): Promise<UserManager> {
    await this.init();
    if (!this.manager) throw new Error('Sign-in is unavailable right now.');
    return this.manager;
  }

  private renew(): Promise<User | null> {
    const manager = this.manager;
    if (!manager) return Promise.resolve(null);
    return (this.renewal ??= this.withRenewalLock(async () => {
      const stored = await manager.getUser().catch(() => null);
      if (
        stored &&
        !stored.expired &&
        (stored.expires_in ?? 0) > 30 &&
        stored.access_token !== this.oidcUser()?.access_token
      ) {
        this.oidcUser.set(stored);
        return stored;
      }
      try {
        const refreshed = await manager.signinSilent();
        this.oidcUser.set(refreshed);
        return refreshed;
      } catch (error) {
        console.warn('[Auth] Could not renew the sign-in session', error);
        this.oidcUser.set(null);
        return null;
      }
    }).finally(() => (this.renewal = null)));
  }

  private withRenewalLock<T>(action: () => Promise<T>): Promise<T> {
    const locks = this.document.defaultView?.navigator?.locks;
    return locks ? (locks.request(RENEWAL_LOCK, action) as Promise<T>) : action();
  }
}

/** Only same-origin API requests receive the player's bearer token. */
export const authTokenInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const location = inject(DOCUMENT).location;
  const url = new URL(request.url, location.href);
  if (
    auth.mode === 'local' ||
    url.origin !== location.origin ||
    !url.pathname.startsWith('/api/') ||
    request.headers.has('Authorization')
  ) {
    return next(request);
  }
  return from(auth.token()).pipe(
    switchMap((token) =>
      next(token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request)
    )
  );
};
