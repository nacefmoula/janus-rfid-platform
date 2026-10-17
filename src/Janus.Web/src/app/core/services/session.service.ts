import { Injectable, inject, signal, computed } from '@angular/core';
import { Subject } from 'rxjs';
import { ValidationSessionService } from './validation-session.service';

export type UserRole = 'ANONYMOUS' | 'TERMINAL' | 'ADMIN';

/**
 * Service de session en mémoire volatile.
 * Conformément aux directives de sécurité :
 * - Aucun token n'est persisté dans localStorage ou sessionStorage.
 * - Tout rafraîchissement réinitialise la session.
 * - Les tokens ne sont jamais codés en dur dans le code source ou le bundle.
 * - Le rôle actif détermine strictement le jeton émis (aucune fuite du jeton Admin en mode Terminal ou Anonyme).
 */
@Injectable({
  providedIn: 'root'
})
export class SessionService {
  private readonly validationSession = inject(ValidationSessionService);

  private readonly terminalTokenSignal = signal<string | null>(null);
  private readonly adminTokenSignal = signal<string | null>(null);
  private readonly activeRoleSignal = signal<UserRole>('ANONYMOUS');
  private readonly sessionVersionSignal = signal<number>(0);

  private readonly sessionChangesSubject = new Subject<UserRole>();
  readonly sessionChanges$ = this.sessionChangesSubject.asObservable();

  readonly terminalToken = computed(() => this.terminalTokenSignal());
  readonly adminToken = computed(() => this.adminTokenSignal());
  readonly activeRole = computed(() => this.activeRoleSignal());
  readonly sessionVersion = computed(() => this.sessionVersionSignal());

  readonly hasTerminalAuth = computed(() => !!this.terminalTokenSignal());
  readonly hasAdminAuth = computed(() => !!this.adminTokenSignal());

  setTerminalToken(token: string | null): void {
    const normalized = token?.trim() || null;
    this.terminalTokenSignal.set(normalized);
    if (normalized && this.activeRoleSignal() === 'ANONYMOUS') {
      this.activeRoleSignal.set('TERMINAL');
    } else if (!normalized && this.activeRoleSignal() === 'TERMINAL') {
      this.activeRoleSignal.set('ANONYMOUS');
    }
    this.notifySessionChanged();
  }

  setAdminToken(token: string | null): void {
    const normalized = token?.trim() || null;
    this.adminTokenSignal.set(normalized);
    if (normalized) {
      this.activeRoleSignal.set('ADMIN');
    } else if (this.activeRoleSignal() === 'ADMIN') {
      this.activeRoleSignal.set(this.terminalTokenSignal() ? 'TERMINAL' : 'ANONYMOUS');
    }
    this.notifySessionChanged();
  }

  setActiveRole(role: UserRole): void {
    this.activeRoleSignal.set(role);
    this.notifySessionChanged();
  }

  /**
   * Vérifie que le rôle demandé dispose bien du jeton correspondant.
   */
  isRoleCompatible(
    role: UserRole,
    terminalToken: string | null = this.terminalTokenSignal(),
    adminToken: string | null = this.adminTokenSignal()
  ): boolean {
    if (role === 'ANONYMOUS') return true;
    if (role === 'TERMINAL') return !!terminalToken?.trim();
    if (role === 'ADMIN') return !!adminToken?.trim();
    return false;
  }

  /**
   * Configure de façon atomique les jetons et le rôle actif après validation de cohérence.
   * Retourne false sans modifier la session si le rôle choisi ne dispose pas du jeton requis.
   */
  configureSession(config: {
    terminalToken: string | null;
    adminToken: string | null;
    role: UserRole;
  }): boolean {
    const normalizedTerminal = config.terminalToken?.trim() || null;
    const normalizedAdmin = config.adminToken?.trim() || null;

    if (!this.isRoleCompatible(config.role, normalizedTerminal, normalizedAdmin)) {
      return false;
    }

    this.terminalTokenSignal.set(normalizedTerminal);
    this.adminTokenSignal.set(normalizedAdmin);
    this.activeRoleSignal.set(config.role);
    this.notifySessionChanged();
    return true;
  }

  clearTokens(): void {
    this.terminalTokenSignal.set(null);
    this.adminTokenSignal.set(null);
    this.activeRoleSignal.set('ANONYMOUS');
    this.notifySessionChanged();
  }

  /**
   * Retourne strictement le jeton associé au rôle actif :
   * - ANONYMOUS : null (aucun jeton envoyé)
   * - TERMINAL  : terminalToken uniquement (jamais adminToken)
   * - ADMIN     : adminToken uniquement (jamais terminalToken)
   */
  getEffectiveToken(preferAdmin = false): string | null {
    const role = this.activeRoleSignal();
    if (role === 'ANONYMOUS') {
      return null;
    }
    if (role === 'ADMIN') {
      return this.adminTokenSignal();
    }
    if (role === 'TERMINAL') {
      return preferAdmin ? null : this.terminalTokenSignal();
    }
    return null;
  }

  private notifySessionChanged(): void {
    const currentRole = this.activeRoleSignal();
    if (currentRole === 'ANONYMOUS') {
      this.validationSession.clear();
    }
    this.sessionVersionSignal.update(v => v + 1);
    this.sessionChangesSubject.next(currentRole);
  }
}
