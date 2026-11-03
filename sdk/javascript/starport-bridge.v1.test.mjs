import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import vm from 'node:vm';

const script = readFileSync(new URL('./starport-bridge.v1.js', import.meta.url), 'utf8');

function createFrame(saved = {}) {
  const sent = [];
  const listeners = new Map();
  const parent = { postMessage: (message, origin) => sent.push({ message, origin }) };
  const window = {
    parent,
    location: { hash: '', pathname: '/index.html', search: '' },
    history: { replaceState() {} },
    sessionStorage: { getItem: (key) => saved[key] ?? null },
    setTimeout,
    clearTimeout,
    queueMicrotask,
    addEventListener: (name, callback) => listeners.set(name, callback),
  };
  vm.runInNewContext(script, { window, URL, URLSearchParams });
  return { bridge: window.StarportBridgeV1, parent, sent,
    message: (origin, source, data) => listeners.get('message')({ origin, source, data }) };
}

test('requests use exact origin, version, nonce and only accept replies from the parent', () => {
  const frame = createFrame();
  const nonce = '1234567890abcdef1234567890abcdef';
  assert.equal(frame.bridge.configure('https://starport.club', nonce), true);
  let response;
  frame.bridge.request('g_1', 'loadSave', '{"slot":"main"}', (value) => response = JSON.parse(value));
  assert.equal(frame.sent[0].origin, 'https://starport.club');
  assert.equal(frame.sent[0].message.nonce, nonce);
  assert.equal(frame.sent[0].message.version, 1);
  frame.message('https://evil.example', frame.parent, {
    protocol: 'starport.bridge', version: 1, type: 'response', id: 'g_1', nonce, ok: true,
  });
  frame.message('https://starport.club', {}, {
    protocol: 'starport.bridge', version: 1, type: 'response', id: 'g_1', nonce, ok: true,
  });
  frame.message('https://starport.club', frame.parent, {
    protocol: 'starport.bridge', version: 1, type: 'response', id: 'g_1', nonce: 'wrong', ok: true,
  });
  assert.equal(response, undefined);
  frame.message('https://starport.club', frame.parent, {
    protocol: 'starport.bridge', version: 1, type: 'response', id: 'g_1', nonce,
    ok: true, result: { revision: 'r1' },
  });
  assert.equal(response.ok, true);
  assert.equal(response.result.revision, 'r1');
});

test('unsupported methods fail before messaging and invalidation ends pending calls', async () => {
  const frame = createFrame();
  const nonce = '1234567890abcdef1234567890abcdef';
  frame.bridge.configure('https://starport.club', nonce);
  let unsupported;
  frame.bridge.request('bad', 'fetchToken', '{}', (value) => unsupported = JSON.parse(value));
  await Promise.resolve();
  assert.equal(unsupported.error.code, 'unsupported-method');
  assert.equal(frame.sent.length, 0);

  let ended;
  frame.bridge.request('good', 'save', '{"slot":"main"}', (value) => ended = JSON.parse(value));
  frame.message('https://starport.club', frame.parent, {
    protocol: 'starport.bridge', version: 1, type: 'invalidate', nonce,
  });
  assert.equal(ended.error.code, 'session-ended');
  let unavailable;
  frame.bridge.request('later', 'loadSave', '{}', (value) => unavailable = JSON.parse(value));
  await Promise.resolve();
  assert.equal(unavailable.error.code, 'unavailable');
});

test('configuration requires an embedded frame and HTTPS except localhost', () => {
  const frame = createFrame();
  assert.equal(frame.bridge.configure('http://example.com', '1234567890abcdef'), false);
  assert.equal(frame.bridge.configure('https://example.com/path', '1234567890abcdef'), false);
  assert.equal(frame.bridge.configure('http://127.0.0.1:8000', 'short'), false);
  assert.equal(frame.bridge.configure('http://127.0.0.1:8000', '1234567890abcdef'), true);
  assert.equal(frame.bridge.configure('https://different.example', '1234567890abcdef'), false);
});

test('bootstrap routing values survive the redirect through session storage', () => {
  const nonce = '1234567890abcdef1234567890abcdef';
  const frame = createFrame({
    'starport.bridge.v1.origin': 'https://starport.club',
    'starport.bridge.v1.nonce': nonce,
  });
  frame.bridge.request('load_1', 'loadSave', '{"slot":"main"}', () => {});
  assert.equal(frame.sent[0].origin, 'https://starport.club');
  assert.equal(frame.sent[0].message.nonce, nonce);
  frame.message('https://starport.club', frame.parent, {
    protocol: 'starport.bridge', version: 1, type: 'response', id: 'load_1', nonce, ok: true,
  });
});
