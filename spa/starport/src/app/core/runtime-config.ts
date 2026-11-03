import { InjectionToken } from '@angular/core';

export interface RuntimeConfig {
  product: 'starport';
  auth: {
    mode: 'local' | 'oidc';
    authority?: string;
    clientId?: string;
    scopes?: string;
    accountUrl?: string;
  };
  features: {
    publishing: boolean;
    cloudSaves: boolean;
  };
}

export const RUNTIME_CONFIG = new InjectionToken<RuntimeConfig>('STARPORT_RUNTIME_CONFIG');

export function parseRuntimeConfig(value: unknown): RuntimeConfig {
  if (typeof value !== 'object' || value === null)
    throw new Error('Invalid Starport configuration.');
  const candidate = value as Partial<RuntimeConfig>;
  if (candidate.product !== 'starport' || !candidate.auth || !candidate.features) {
    throw new Error('The server returned an unexpected product or incomplete configuration.');
  }
  if (candidate.auth.mode !== 'local' && candidate.auth.mode !== 'oidc') {
    throw new Error('The server returned an unsupported sign-in mode.');
  }
  if (candidate.auth.mode === 'oidc' && (!candidate.auth.authority || !candidate.auth.clientId)) {
    throw new Error('The server did not provide its OIDC settings.');
  }
  return candidate as RuntimeConfig;
}
