import { HttpClient } from '@angular/common/http';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { LaunchResponse, PlayerBridge, prepareEmbedUrl } from './player-bridge';

const launch: LaunchResponse = {
  embedUrl: 'https://game.example.net/__launch#ticket=opaque-ticket',
  contentOrigin: 'https://game.example.net',
  bridgeNonce: '1234567890abcdef1234567890abcdef',
  expiresAt: '2030-01-01T00:00:00Z',
  canSave: true,
};

describe('player launch URL', () => {
  it('keeps the ticket on the game origin and adds only bridge routing values', () => {
    const url = prepareEmbedUrl(launch, 'https://app.starport.club');
    expect(url.origin).toBe('https://game.example.net');
    expect(new URLSearchParams(url.hash.slice(1)).get('ticket')).toBe('opaque-ticket');
    expect(new URLSearchParams(url.hash.slice(1)).get('starport_origin')).toBe(
      'https://app.starport.club'
    );
    expect(new URLSearchParams(url.hash.slice(1)).get('starport_nonce')).toBe(launch.bridgeNonce);
  });

  it('rejects same-origin and mismatched content hosts', () => {
    expect(() =>
      prepareEmbedUrl(
        { ...launch, contentOrigin: 'https://app.starport.club' },
        'https://app.starport.club'
      )
    ).toThrow();
    expect(() =>
      prepareEmbedUrl(
        { ...launch, embedUrl: 'https://evil.example/__launch#ticket=bad' },
        'https://app.starport.club'
      )
    ).toThrow();
    expect(() =>
      prepareEmbedUrl(
        { ...launch, embedUrl: 'https://game.example.net/__launch' },
        'https://app.starport.club'
      )
    ).toThrow();
  });

  it('accepts an isolated localhost subdomain for local game delivery', () => {
    const localLaunch = {
      ...launch,
      embedUrl: 'http://g-c395cf47.localhost:5084/__launch#ticket=opaque-ticket',
      contentOrigin: 'http://g-c395cf47.localhost:5084',
    };
    expect(prepareEmbedUrl(localLaunch, 'http://localhost:4200').origin).toBe(
      localLaunch.contentOrigin
    );
    expect(() =>
      prepareEmbedUrl(
        { ...localLaunch, contentOrigin: 'http://g-c395cf47.localhost.evil.test:5084' },
        'http://localhost:4200'
      )
    ).toThrow();
  });

  it('accepts local same-site lvh.me subdomains while rejecting lookalikes', () => {
    const localLaunch = {
      ...launch,
      embedUrl: 'http://g-c395cf47.games.lvh.me:5084/__launch#ticket=opaque-ticket',
      contentOrigin: 'http://g-c395cf47.games.lvh.me:5084',
    };
    expect(prepareEmbedUrl(localLaunch, 'http://app.lvh.me:4200').origin).toBe(
      localLaunch.contentOrigin
    );
    expect(() =>
      prepareEmbedUrl(
        { ...localLaunch, contentOrigin: 'http://g-c395cf47.games.lvh.me.evil.test:5084' },
        'http://app.lvh.me:4200'
      )
    ).toThrow();
  });
});

describe('parent bridge', () => {
  it('ignores wrong sources, origins, nonces and methods before responding', async () => {
    const replies: { data: Record<string, unknown>; origin: string }[] = [];
    const child = {
      postMessage: (data: Record<string, unknown>, origin: string) =>
        replies.push({ data, origin }),
    };
    let listener: ((event: MessageEvent) => void) | undefined;
    const appWindow = {
      addEventListener: (_type: string, callback: (event: MessageEvent) => void) => {
        listener = callback;
      },
      removeEventListener: () => {
        listener = undefined;
      },
    } as unknown as Window;
    const bridge = new PlayerBridge(
      { contentWindow: child } as unknown as HTMLIFrameElement,
      appWindow,
      {} as HttpClient,
      'c395cf47-498f-48db-b5ff-e55087c06946',
      launch,
      () => true
    );
    const request = {
      protocol: 'starport.bridge',
      version: 1,
      type: 'request',
      id: 'r1',
      method: 'capabilities',
      payload: {},
      nonce: launch.bridgeNonce,
    };
    const emit = (source: unknown, origin: string, data: unknown) =>
      listener?.(new MessageEvent('message', { source: source as Window, origin, data }));
    emit({}, launch.contentOrigin, request);
    emit(child, 'https://evil.example', request);
    emit(child, launch.contentOrigin, { ...request, nonce: 'wrong' });
    emit(child, launch.contentOrigin, { ...request, method: 'getToken' });
    expect(replies).toHaveLength(0);
    emit(child, launch.contentOrigin, request);
    await vi.waitFor(() => expect(replies).toHaveLength(1));
    expect(replies[0].origin).toBe(launch.contentOrigin);
    expect(replies[0].data).toMatchObject({
      protocol: 'starport.bridge',
      version: 1,
      type: 'response',
      id: 'r1',
      nonce: launch.bridgeNonce,
      ok: true,
    });
    bridge.dispose();
  });

  it('marks publisher-preview save calls for server-side preview authorization', async () => {
    const urls: string[] = [];
    const child = { postMessage: () => undefined };
    let listener: ((event: MessageEvent) => void) | undefined;
    const appWindow = {
      addEventListener: (_type: string, callback: (event: MessageEvent) => void) => {
        listener = callback;
      },
      removeEventListener: () => {
        listener = undefined;
      },
    } as unknown as Window;
    const http = {
      get: (url: string) => {
        urls.push(url);
        return of(null);
      },
    } as unknown as HttpClient;
    const gameId = 'c395cf47-498f-48db-b5ff-e55087c06946';
    const bridge = new PlayerBridge(
      { contentWindow: child } as unknown as HTMLIFrameElement,
      appWindow,
      http,
      gameId,
      launch,
      () => true,
      true
    );
    listener?.(
      new MessageEvent('message', {
        source: child as unknown as Window,
        origin: launch.contentOrigin,
        data: {
          protocol: 'starport.bridge',
          version: 1,
          type: 'request',
          id: 'r1',
          method: 'loadSave',
          payload: { slot: 'main' },
          nonce: launch.bridgeNonce,
        },
      })
    );
    await vi.waitFor(() => expect(urls).toEqual([`/api/games/${gameId}/saves/main?preview=true`]));
    bridge.dispose();
  });
});
