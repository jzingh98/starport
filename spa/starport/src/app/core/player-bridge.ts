import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { firstValueFrom, timeout } from 'rxjs';

const PROTOCOL = 'starport.bridge';
const MAX_MESSAGE_BYTES = 1_200_000;
const MAX_SAVE_BYTES = 1_048_576;
const MAX_PENDING = 32;
const SLOT = /^[A-Za-z0-9_-]{1,64}$/;
const ID = /^[A-Za-z0-9_-]{1,64}$/;
const NONCE = /^[A-Za-z0-9_-]{16,128}$/;
const METHODS = new Set(['loadSave', 'save', 'listSlots', 'deleteSave', 'capabilities']);

export interface LaunchResponse {
  embedUrl: string;
  contentOrigin: string;
  bridgeNonce: string;
  expiresAt: string;
  canSave: boolean;
}

interface BridgeRequest {
  protocol: string;
  version: 1;
  type: 'request';
  id: string;
  method: string;
  payload: Record<string, unknown>;
  nonce: string;
}

export function prepareEmbedUrl(launch: LaunchResponse, appOrigin: string): URL {
  const origin = new URL(launch.contentOrigin);
  const localContentHost =
    ['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname) ||
    origin.hostname.endsWith('.localhost') ||
    origin.hostname === 'lvh.me' ||
    origin.hostname.endsWith('.lvh.me');
  if (
    origin.origin !== launch.contentOrigin ||
    origin.origin === appOrigin ||
    (origin.protocol !== 'https:' && !(origin.protocol === 'http:' && localContentHost))
  ) {
    throw new Error('The game content origin is invalid.');
  }
  if (!NONCE.test(launch.bridgeNonce)) throw new Error('The game launch nonce is invalid.');
  const url = new URL(launch.embedUrl);
  if (url.origin !== origin.origin) throw new Error('The game URL has the wrong origin.');
  const fragment = new URLSearchParams(url.hash.slice(1));
  if (!fragment.get('ticket')) throw new Error('The game launch ticket is missing.');
  if (fragment.has('starport_origin') || fragment.has('starport_nonce'))
    throw new Error('The game URL contains unexpected bridge settings.');
  fragment.set('starport_origin', appOrigin);
  fragment.set('starport_nonce', launch.bridgeNonce);
  url.hash = fragment.toString();
  return url;
}

function parseRequest(value: unknown): BridgeRequest | null {
  if (!value || typeof value !== 'object') return null;
  let size: number;
  try {
    size = JSON.stringify(value).length;
  } catch {
    return null;
  }
  if (size > MAX_MESSAGE_BYTES) return null;
  const request = value as Partial<BridgeRequest>;
  if (
    request.protocol !== PROTOCOL ||
    request.version !== 1 ||
    request.type !== 'request' ||
    typeof request.id !== 'string' ||
    !ID.test(request.id) ||
    typeof request.method !== 'string' ||
    !METHODS.has(request.method) ||
    !request.payload ||
    typeof request.payload !== 'object' ||
    Array.isArray(request.payload) ||
    typeof request.nonce !== 'string'
  )
    return null;
  const payload = request.payload;
  const allowed =
    request.method === 'save'
      ? ['slot', 'data', 'expectedRevision', 'requestKey']
      : request.method === 'deleteSave'
        ? ['slot', 'expectedRevision']
        : request.method === 'loadSave'
          ? ['slot']
          : [];
  if (Object.keys(payload).some((key) => !allowed.includes(key))) return null;
  if (
    request.method !== 'capabilities' &&
    request.method !== 'listSlots' &&
    (typeof payload['slot'] !== 'string' || !SLOT.test(payload['slot']))
  )
    return null;
  if (request.method === 'save') {
    if (!('data' in payload) || payload['data'] === undefined) return null;
    try {
      if (JSON.stringify(payload['data']).length > MAX_SAVE_BYTES) return null;
    } catch {
      return null;
    }
    if (payload['expectedRevision'] !== null && typeof payload['expectedRevision'] !== 'string')
      return null;
    if (
      payload['requestKey'] !== undefined &&
      (typeof payload['requestKey'] !== 'string' || payload['requestKey'].length > 128)
    )
      return null;
  }
  if (
    request.method === 'deleteSave' &&
    (typeof payload['expectedRevision'] !== 'string' || !payload['expectedRevision'])
  )
    return null;
  return request as BridgeRequest;
}

function errorCode(error: unknown): { code: string; message: string } {
  if (error instanceof HttpErrorResponse) {
    if (error.status === 409 && error.error?.code === 'save-limit')
      return { code: 'quota-exceeded', message: 'This game has reached its save slot limit.' };
    if (error.status === 409 || error.status === 412)
      return { code: 'conflict', message: 'The save changed on another device.' };
    if (error.status === 401)
      return { code: 'sign-in-required', message: 'Sign in to save this game.' };
    if (error.status === 403)
      return { code: 'forbidden', message: 'Saving is unavailable for this launch.' };
    if (error.status === 413) return { code: 'quota-exceeded', message: 'This save is too large.' };
    if (error.status === 429) return { code: 'rate-limited', message: 'Wait before saving again.' };
  }
  return { code: 'unavailable', message: 'Cloud saves are unavailable right now.' };
}

/** A game frame can request only its own game's saves. The app makes all authenticated API calls. */
export class PlayerBridge {
  private active = true;
  private pending = 0;
  private readonly listener = (event: MessageEvent) => void this.receive(event);

  constructor(
    private readonly frame: HTMLIFrameElement,
    private readonly appWindow: Window,
    private readonly http: HttpClient,
    private readonly gameId: string,
    private readonly launch: LaunchResponse,
    private readonly canSaveNow: () => boolean,
    private readonly preview = false
  ) {
    appWindow.addEventListener('message', this.listener);
  }

  dispose(): void {
    if (!this.active) return;
    this.send({ type: 'invalidate' });
    this.active = false;
    this.appWindow.removeEventListener('message', this.listener);
  }

  private send(body: Record<string, unknown>): void {
    this.frame.contentWindow?.postMessage(
      {
        protocol: PROTOCOL,
        version: 1,
        nonce: this.launch.bridgeNonce,
        ...body,
      },
      this.launch.contentOrigin
    );
  }

  private receive(event: MessageEvent): void {
    if (
      !this.active ||
      event.source !== this.frame.contentWindow ||
      event.origin !== this.launch.contentOrigin
    )
      return;
    const request = parseRequest(event.data);
    if (!request || request.nonce !== this.launch.bridgeNonce) return;
    if (this.pending >= MAX_PENDING) {
      this.send({
        type: 'response',
        id: request.id,
        ok: false,
        error: { code: 'busy', message: 'Too many save requests.' },
      });
      return;
    }
    this.pending++;
    void this.respond(request).finally(() => this.pending--);
  }

  private async respond(request: BridgeRequest): Promise<void> {
    try {
      if (!this.launch.canSave || !this.canSaveNow()) {
        if (request.method !== 'capabilities')
          throw { code: 'sign-in-required', message: 'Sign in to use cloud saves.' };
      }
      const result = await this.execute(request);
      if (this.active) this.send({ type: 'response', id: request.id, ok: true, result });
    } catch (error) {
      const safe =
        error && typeof error === 'object' && 'code' in error && 'message' in error
          ? error
          : errorCode(error);
      if (this.active) this.send({ type: 'response', id: request.id, ok: false, error: safe });
    }
  }

  private async execute(request: BridgeRequest): Promise<unknown> {
    if (request.method === 'capabilities')
      return { canSave: this.launch.canSave && this.canSaveNow(), maxSlots: 10 };
    const base = `/api/games/${encodeURIComponent(this.gameId)}/saves`;
    const query = this.preview ? '?preview=true' : '';
    const slot = request.payload['slot'] as string;
    const url = `${base}/${encodeURIComponent(slot)}${query}`;
    if (request.method === 'listSlots')
      return firstValueFrom(this.http.get<unknown>(`${base}${query}`).pipe(timeout(10000)));
    if (request.method === 'loadSave') {
      try {
        return await firstValueFrom(this.http.get<unknown>(url).pipe(timeout(10000)));
      } catch (error) {
        if (
          error instanceof HttpErrorResponse &&
          error.status === 404 &&
          error.error?.code === 'save-not-found'
        )
          return null;
        throw error;
      }
    }
    const headers = new HttpHeaders(
      request.method === 'save' && request.payload['expectedRevision'] === null
        ? { 'If-None-Match': '*' }
        : { 'If-Match': request.payload['expectedRevision'] as string }
    );
    if (request.method === 'save') {
      return firstValueFrom(
        this.http
          .put<unknown>(
            url,
            {
              data: request.payload['data'],
              requestKey: request.payload['requestKey'] || undefined,
            },
            { headers }
          )
          .pipe(timeout(10000))
      );
    }
    await firstValueFrom(this.http.delete(url, { headers }).pipe(timeout(10000)));
    return { deleted: true };
  }
}
