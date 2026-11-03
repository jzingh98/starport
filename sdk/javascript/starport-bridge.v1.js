/* Starport game-frame bridge, protocol v1. Copy into a Godot Web export and load before Godot. */
(function (window) {
  'use strict';

  const PROTOCOL = 'starport.bridge';
  const VERSION = 1;
  const MAX_MESSAGE_BYTES = 1_200_000;
  const MAX_PENDING = 32;
  const TIMEOUT_MS = 15_000;
  const METHODS = new Set(['loadSave', 'save', 'listSlots', 'deleteSave', 'capabilities']);
  const pending = new Map();
  let configuration = null;

  function failure(code, message) {
    return { ok: false, error: { code, message } };
  }

  function complete(id, response) {
    const item = pending.get(id);
    if (!item) return;
    pending.delete(id);
    window.clearTimeout(item.timer);
    item.callback(JSON.stringify({ id, ...response }));
  }

  function configure(parentOrigin, launchNonce) {
    if (configuration) return false;
    if (window.parent === window) return false;
    try {
      const parsed = new URL(parentOrigin);
      if (parsed.origin !== parentOrigin || !['https:', 'http:'].includes(parsed.protocol)) return false;
      if (parsed.protocol === 'http:' && !['localhost', '127.0.0.1', '[::1]'].includes(parsed.hostname)) return false;
      if (typeof launchNonce !== 'string' || !/^[A-Za-z0-9_-]{16,128}$/.test(launchNonce)) return false;
      configuration = Object.freeze({ parentOrigin, launchNonce });
      return true;
    } catch {
      return false;
    }
  }

  function request(id, method, payloadJson, callback) {
    if (typeof callback !== 'function') throw new TypeError('A response callback is required');
    const reject = (code, message) => window.queueMicrotask(() => callback(JSON.stringify({ id, ...failure(code, message) })));
    if (!configuration) return reject('unavailable', 'Starport bridge is not configured');
    if (typeof id !== 'string' || !/^[A-Za-z0-9_-]{1,64}$/.test(id) || pending.has(id))
      return reject('invalid-request', 'Invalid or duplicate request ID');
    if (!METHODS.has(method)) return reject('unsupported-method', 'Unsupported bridge method');
    if (typeof payloadJson !== 'string' || payloadJson.length > MAX_MESSAGE_BYTES)
      return reject('invalid-request', 'Payload is too large');
    let payload;
    try {
      payload = JSON.parse(payloadJson);
      if (!payload || typeof payload !== 'object' || Array.isArray(payload)) throw new Error();
    } catch {
      return reject('invalid-request', 'Payload must be a JSON object');
    }
    if (pending.size >= MAX_PENDING) return reject('busy', 'Too many pending requests');

    const timer = window.setTimeout(() => complete(id, failure('timeout', 'Starport did not respond')), TIMEOUT_MS);
    pending.set(id, { callback, timer });
    window.parent.postMessage({
      protocol: PROTOCOL, version: VERSION, type: 'request', id, method, payload,
      nonce: configuration.launchNonce,
    }, configuration.parentOrigin);
  }

  window.addEventListener('message', (event) => {
    if (!configuration || event.source !== window.parent || event.origin !== configuration.parentOrigin) return;
    const data = event.data;
    if (!data || typeof data !== 'object' || data.protocol !== PROTOCOL ||
        data.version !== VERSION || data.nonce !== configuration.launchNonce) return;
    try {
      if (JSON.stringify(data).length > MAX_MESSAGE_BYTES) return;
    } catch { return; }
    if (data.type === 'invalidate') {
      configuration = null;
      for (const id of pending.keys()) complete(id, failure('session-ended', 'The game session ended'));
      return;
    }
    if (data.type !== 'response' || typeof data.id !== 'string' || typeof data.ok !== 'boolean') return;
    if (data.ok) complete(data.id, { ok: true, result: data.result ?? null });
    else if (data.error && typeof data.error.code === 'string')
      complete(data.id, failure(data.error.code, String(data.error.message ?? 'Request failed')));
  });

  // The trusted bootstrap passes these non-secret values in the iframe fragment.
  // The parent must still validate origin, source, nonce and active launch on every request.
  const fragment = new URLSearchParams(window.location.hash.slice(1));
  if (fragment.has('starport_origin') && fragment.has('starport_nonce')) {
    configure(fragment.get('starport_origin'), fragment.get('starport_nonce'));
  } else {
    // The delivery bootstrap removes the one-use ticket before loading index.html.
    // It keeps these two non-secret routing values for the redirect in this frame.
    try {
      configure(window.sessionStorage.getItem('starport.bridge.v1.origin'),
        window.sessionStorage.getItem('starport.bridge.v1.nonce'));
    } catch { /* Third-party storage may be unavailable; the parent can pass a fragment. */ }
  }

  window.StarportBridgeV1 = Object.freeze({ version: VERSION, configure, request });
})(window);
