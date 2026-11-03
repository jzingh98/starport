import { HttpClient } from '@angular/common/http';
import { DOCUMENT, Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthService } from './auth.service';

const LAST_WORKSPACE_KEY = 'starport.lastWorkspace';

export interface Workspace {
  id: string;
  slug: string;
  name: string;
  kind: 'Personal' | 'Team';
  role: 'Owner' | 'Admin' | 'Member' | 'Viewer';
  canPublish: boolean;
  paidThrough?: string | null;
}

@Injectable({ providedIn: 'root' })
export class WorkspaceService {
  private readonly http = inject(HttpClient);
  private readonly auth = inject(AuthService);
  private readonly document = inject(DOCUMENT);
  private readonly rows = signal<Workspace[]>([]);
  private readonly selectedId = signal<string | null>(null);
  private readonly state = signal<'idle' | 'loading' | 'ready' | 'error'>('idle');
  private loading: Promise<void> | null = null;

  readonly workspaces = this.rows.asReadonly();
  readonly status = this.state.asReadonly();
  readonly current = computed(() => this.rows().find((w) => w.id === this.selectedId()) ?? null);
  readonly canPublish = computed(() => this.current()?.canPublish === true);

  ensureLoaded(): Promise<void> {
    return (this.loading ??= this.fetch());
  }

  reload(): Promise<void> {
    return (this.loading = this.fetch());
  }

  select(id: string): boolean {
    if (!this.rows().some((workspace) => workspace.id === id)) return false;
    this.selectedId.set(id);
    try {
      this.document.defaultView?.localStorage.setItem(LAST_WORKSPACE_KEY, id);
    } catch {
      // Remembering a workspace is a convenience, not an access grant.
    }
    return true;
  }

  clear(): void {
    this.rows.set([]);
    this.selectedId.set(null);
    this.state.set('idle');
    this.loading = null;
  }

  private async fetch(): Promise<void> {
    await this.auth.init();
    if (!this.auth.isAuthenticated()) {
      this.clear();
      return;
    }
    this.state.set('loading');
    try {
      const workspaces = await firstValueFrom(this.http.get<Workspace[]>('/api/workspaces'));
      this.rows.set(workspaces);
      let last: string | null = null;
      try {
        last = this.document.defaultView?.localStorage.getItem(LAST_WORKSPACE_KEY) ?? null;
      } catch {
        // Private browsing can disable storage.
      }
      const chosen =
        workspaces.find((w) => w.id === this.selectedId()) ??
        workspaces.find((w) => w.id === last) ??
        workspaces.find((w) => w.kind === 'Personal') ??
        workspaces[0];
      this.selectedId.set(chosen?.id ?? null);
      this.state.set('ready');
    } catch (error) {
      console.error('[Workspaces] Could not load workspaces', error);
      this.rows.set([]);
      this.selectedId.set(null);
      this.state.set('error');
      this.loading = null;
    }
  }
}
