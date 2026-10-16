import { Injectable, signal, computed } from '@angular/core';

export type UserRole = 'ANONYMOUS' | 'TERMINAL' | 'ADMIN';

/**
 * Service de session en mémoire volatile.
 * Conformément aux directives de sécurité :
 * - Aucun token n'est persisté dans localStorage ou sessionStorage.
 * - Tout rafraîchissement réinitialise la session.
 * - Les tokens ne sont jamais codés en dur dans le code source ou le bundle.
 */
@Injectable({
  providedIn: 'root'
})
export class SessionService {
  private readonly terminalTokenSignal = signal<string | null>(null);
  private readonly adminTokenSignal = signal<string | null>(null);
  private readonly activeRoleSignal = signal<UserRole>('ANONYMOUS');

  readonly terminalToken = computed(() => this.terminalTokenSignal());
  readonly adminToken = computed(() => this.adminTokenSignal());
  readonly activeRole = computed(() => this.activeRoleSignal());

  readonly hasTerminalAuth = computed(() => !!this.terminalTokenSignal());
  readonly hasAdminAuth = computed(() => !!this.adminTokenSignal());

  setTerminalToken(token: string | null): void {
    this.terminalTokenSignal.set(token?.trim() || null);
    if (token && this.activeRoleSignal() === 'ANONYMOUS') {
      this.activeRoleSignal.set('TERMINAL');
    }
  }

  setAdminToken(token: string | null): void {
    this.adminTokenSignal.set(token?.trim() || null);
    if (token) {
      this.activeRoleSignal.set('ADMIN');
    }
  }

  setActiveRole(role: UserRole): void {
    this.activeRoleSignal.set(role);
  }

  clearTokens(): void {
    this.terminalTokenSignal.set(null);
    this.adminTokenSignal.set(null);
    this.activeRoleSignal.set('ANONYMOUS');
  }

  /**
   * Retourne le token approprié selon le contexte de la requête.
   * Si le rôle actif est ADMIN, privilégie le token admin, sinon le token terminal.
   */
  getEffectiveToken(preferAdmin = false): string | null {
    if (preferAdmin) {
      return this.adminTokenSignal();
    }
    const role = this.activeRoleSignal();
    if (role === 'ADMIN') {
      return this.adminTokenSignal();
    }
    if (role === 'TERMINAL') {
      return this.terminalTokenSignal();
    }
    return null;
  }
}
