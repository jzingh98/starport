import { bootstrapApplication } from '@angular/platform-browser';
import { provideHttpClient, withFetch, withInterceptors } from '@angular/common/http';
import { provideRouter } from '@angular/router';
import { App, routes } from './app/app';
import { authTokenInterceptor } from './app/core/auth.service';
import { parseRuntimeConfig, RUNTIME_CONFIG } from './app/core/runtime-config';

async function main(): Promise<void> {
  const response = await fetch('/api/config');
  if (!response.ok) throw new Error('Starport could not load its configuration.');
  const config = parseRuntimeConfig(await response.json());
  await bootstrapApplication(App, {
    providers: [
      { provide: RUNTIME_CONFIG, useValue: config },
      provideHttpClient(withFetch(), withInterceptors([authTokenInterceptor])),
      provideRouter(routes),
    ],
  });
}

main().catch((error: unknown) => {
  console.error(error);
  const host = document.querySelector('app-root');
  if (host) host.textContent = 'Starport could not start. Please try again later.';
});
