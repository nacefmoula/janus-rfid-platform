import { Component, EventEmitter, Output, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavigationEnd, Router } from '@angular/router';
import { Subscription, filter } from 'rxjs';
import { SessionService, UserRole } from '../../../core/services/session.service';
import { JanusApiService } from '../../../core/services/api.service';
import { NotificationService } from '../../../core/services/notification.service';

@Component({
  selector: 'app-header',
  standalone: true,
  imports: [CommonModule, FormsModule],
  template: `
    <header class="app-header">
      <div class="header-left">
        <!-- Bouton hamburger pour affichage responsive -->
        <button
          type="button"
          class="mobile-menu-btn"
          (click)="toggleSidebar.emit()"
          aria-label="Ouvrir le menu de navigation">
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <line x1="3" y1="12" x2="21" y2="12"/>
            <line x1="3" y1="6" x2="21" y2="6"/>
            <line x1="3" y1="18" x2="21" y2="18"/>
          </svg>
        </button>

        <div class="title-wrap">
          <h1 class="page-title">{{ currentPageTitle() }}</h1>
          <span class="page-subtitle">{{ currentPageSubtitle() }}</span>
        </div>
      </div>

      <div class="header-right">
        <!-- Indicateur de santé de l'API Janus -->
        <div class="health-pill" [class.healthy]="apiStatus() === 'healthy'" [class.unhealthy]="apiStatus() !== 'healthy'">
          <span class="health-dot" aria-hidden="true"></span>
          <span class="health-label">API Janus : <strong>{{ apiStatus() === 'healthy' ? 'En ligne (Port 5080)' : 'Non joignable' }}</strong></span>
        </div>

        <!-- Bouton de gestion de la session en mémoire -->
        <button
          type="button"
          class="btn btn-secondary session-trigger"
          (click)="toggleModal()"
          title="Paramétrer les jetons de démonstration">
          <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M21 2l-2 2m-1.5 1.5L14 9l-1.5-1.5L10 10l1.5 1.5-4 4H4v3h3l4-4 1.5 1.5 2.5-2.5L15 10l3.5-3.5L20 8l2-2-1-1z"/>
          </svg>
          <span>Session & Rôles</span>
        </button>
      </div>
    </header>

    <!-- Modal des jetons de test en mémoire vive -->
    @if (showModal()) {
      <div class="modal-backdrop" (click)="toggleModal()">
        <div class="modal-dialog" (click)="$event.stopPropagation()" role="dialog" aria-modal="true" aria-labelledby="modalTitle">
          <div class="modal-header">
            <div class="modal-title-wrap">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="text-primary" aria-hidden="true">
                <rect x="3" y="11" width="18" height="11" rx="2" ry="2"/>
                <path d="M7 11V7a5 5 0 0 1 10 0v4"/>
              </svg>
              <h3 id="modalTitle">Configuration de Session d'Exploitation</h3>
            </div>
            <button type="button" class="close-btn" (click)="toggleModal()" aria-label="Fermer la boîte de dialogue">✕</button>
          </div>

          <div class="modal-body">
            <div class="notice-box">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="notice-svg" aria-hidden="true">
                <circle cx="12" cy="12" r="10"/>
                <line x1="12" y1="16" x2="12" y2="12"/>
                <line x1="12" y1="8" x2="12.01" y2="8"/>
              </svg>
              <p>
                <strong>Sécurité & Confidentialité :</strong> Aucun jeton n'est persisté sur le disque ou dans le navigateur (pas de <code>localStorage</code>).
                Les identifiants restent en mémoire vive pour tester les contrôles d'accès réels de l'API Janus.
              </p>
            </div>

            <div class="form-group">
              <label class="form-label" for="terminalToken">Jeton Opérateur Borne (TerminalAuth:DemoToken)</label>
              <input
                id="terminalToken"
                type="password"
                class="form-input"
                placeholder="Saisir le jeton opérateur borne (depuis .env)"
                [ngModel]="tempTerminalToken"
                (ngModelChange)="onTerminalTokenInput($event)"
              />
              <span class="field-hint">Autorise la validation des titres et la consultation des cartes.</span>
            </div>

            <div class="form-group">
              <label class="form-label" for="adminToken">Jeton Administrateur (AdminAuth:DemoToken)</label>
              <input
                id="adminToken"
                type="password"
                class="form-input"
                placeholder="Saisir le jeton administrateur (depuis .env)"
                [ngModel]="tempAdminToken"
                (ngModelChange)="onAdminTokenInput($event)"
              />
              <span class="field-hint">Autorise les recharges de solde, révocations et l'audit.</span>
            </div>

            <div class="form-group">
              <label class="form-label">Rôle d'affichage actif</label>
              <div class="role-grid">
                <button
                  type="button"
                  class="role-card"
                  [class.active]="selectedRole === 'ANONYMOUS'"
                  (click)="selectRole('ANONYMOUS')">
                  <strong>Visiteur</strong>
                  <small>Lecture publique seule</small>
                </button>
                <button
                  type="button"
                  class="role-card"
                  [class.active]="selectedRole === 'TERMINAL'"
                  (click)="selectRole('TERMINAL')">
                  <strong>Opérateur Borne</strong>
                  <small>Validation de trajets</small>
                </button>
                <button
                  type="button"
                  class="role-card"
                  [class.active]="selectedRole === 'ADMIN'"
                  (click)="selectRole('ADMIN')">
                  <strong>Administrateur</strong>
                  <small>Gestion complète & Audit</small>
                </button>
              </div>
            </div>

            @if (sessionError()) {
              <div class="session-error-box" role="alert">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/>
                  <line x1="12" y1="9" x2="12" y2="13"/>
                  <line x1="12" y1="17" x2="12.01" y2="17"/>
                </svg>
                <span>{{ sessionError() }}</span>
              </div>
            }

            <div class="helpers-row">
              <button type="button" class="btn-text-danger" (click)="clearTokens()">
                Purger la session
              </button>
            </div>
          </div>

          <div class="modal-footer">
            <button type="button" class="btn btn-secondary" (click)="toggleModal()">Annuler</button>
            <button type="button" class="btn btn-primary" (click)="applySession()">Enregistrer la session</button>
          </div>
        </div>
      </div>
    }
  `,
  styles: [`
    .app-header {
      height: 64px;
      background-color: var(--bg-surface);
      border-bottom: 1px solid var(--border-light);
      padding: 0 28px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      position: sticky;
      top: 0;
      z-index: 100;
      box-shadow: var(--shadow-xs);
    }

    .header-left {
      display: flex;
      align-items: center;
      gap: 12px;
      min-width: 0;
    }

    .mobile-menu-btn {
      display: none;
      background: transparent;
      border: 1px solid var(--border-light);
      border-radius: var(--radius-sm);
      color: var(--color-text-main);
      padding: 6px;
      cursor: pointer;
      align-items: center;
      justify-content: center;
    }
    .mobile-menu-btn:hover {
      background-color: var(--bg-surface-subtle);
    }

    .title-wrap {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }

    .page-title {
      font-size: 1.0625rem;
      font-weight: 700;
      color: var(--color-text-main);
      line-height: 1.25;
      letter-spacing: -0.01em;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .page-subtitle {
      font-size: 0.75rem;
      color: var(--color-text-dim);
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .header-right {
      display: flex;
      align-items: center;
      gap: 14px;
      flex-shrink: 0;
    }

    .health-pill {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 5px 12px;
      border-radius: var(--radius-full);
      font-size: 0.75rem;
      background-color: var(--bg-surface-subtle);
      border: 1px solid var(--border-light);
    }
    .health-dot {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      flex-shrink: 0;
    }
    .health-pill.healthy .health-dot {
      background-color: var(--color-success);
    }
    .health-pill.unhealthy .health-dot {
      background-color: var(--color-danger);
    }

    .session-trigger {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      font-size: 0.75rem;
      padding: 6px 12px;
    }

    /* Modal de configuration */
    .modal-backdrop {
      position: fixed;
      inset: 0;
      background: rgba(15, 23, 42, 0.55);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 1100;
      backdrop-filter: blur(2px);
      padding: 16px;
    }

    .modal-dialog {
      background: var(--bg-surface);
      border-radius: var(--radius-lg);
      width: 100%;
      max-width: 500px;
      box-shadow: var(--shadow-lg);
      overflow: hidden;
      border: 1px solid var(--border-light);
      animation: modalFadeIn 0.15s ease-out;
    }
    @keyframes modalFadeIn {
      from { transform: scale(0.97); opacity: 0; }
      to { transform: scale(1); opacity: 1; }
    }

    .modal-header {
      padding: 14px 20px;
      border-bottom: 1px solid var(--border-subtle);
      display: flex;
      align-items: center;
      justify-content: space-between;
      background-color: var(--bg-surface-subtle);
    }
    .modal-title-wrap {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .modal-header h3 {
      font-size: 0.9375rem;
      font-weight: 700;
      color: var(--color-text-main);
    }
    .close-btn {
      background: transparent;
      border: none;
      font-size: 1.1rem;
      color: var(--color-text-dim);
      cursor: pointer;
      padding: 4px;
      border-radius: var(--radius-sm);
    }
    .close-btn:hover {
      color: var(--color-text-main);
      background-color: var(--bg-surface-hover);
    }

    .modal-body {
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 14px;
    }

    .notice-box {
      background-color: var(--color-info-bg);
      border: 1px solid var(--color-info-border);
      border-radius: var(--radius-md);
      padding: 10px 12px;
      display: flex;
      gap: 10px;
      color: var(--color-info-text);
      font-size: 0.75rem;
      line-height: 1.45;
    }
    .notice-svg {
      flex-shrink: 0;
      color: var(--color-info);
      margin-top: 2px;
    }

    .form-group {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .field-hint {
      font-size: 0.6875rem;
      color: var(--color-text-dim);
    }

    .role-grid {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      gap: 8px;
    }
    .role-card {
      padding: 8px 6px;
      background: var(--bg-surface-subtle);
      border: 1px solid var(--border-light);
      border-radius: var(--radius-md);
      cursor: pointer;
      text-align: center;
      transition: all 0.15s ease;
    }
    .role-card strong {
      display: block;
      font-size: 0.75rem;
      color: var(--color-text-main);
    }
    .role-card small {
      display: block;
      font-size: 0.65rem;
      color: var(--color-text-muted);
      margin-top: 2px;
    }
    .role-card.active {
      background-color: var(--color-primary-light);
      border-color: var(--color-primary);
    }
    .role-card.active strong {
      color: var(--color-primary);
    }

    .session-error-box {
      background-color: var(--color-danger-bg);
      border: 1px solid var(--color-danger-border);
      color: var(--color-danger-text);
      border-radius: var(--radius-md);
      padding: 8px 12px;
      font-size: 0.75rem;
      font-weight: 600;
      display: flex;
      align-items: center;
      gap: 8px;
    }

    .helpers-row {
      display: flex;
      justify-content: flex-end;
      align-items: center;
      margin-top: 4px;
    }
    .btn-text-danger {
      background: none;
      border: none;
      color: var(--color-danger);
      font-size: 0.75rem;
      font-weight: 600;
      cursor: pointer;
      padding: 4px 0;
    }
    .btn-text-danger:hover {
      text-decoration: underline;
    }

    .modal-footer {
      padding: 12px 20px;
      background-color: var(--bg-surface-subtle);
      border-top: 1px solid var(--border-light);
      display: flex;
      justify-content: flex-end;
      gap: 10px;
    }

    /* Media query responsive */
    @media (max-width: 1023px) {
      .mobile-menu-btn {
        display: inline-flex;
      }
      .app-header {
        padding: 0 16px;
      }
      .health-label {
        display: none;
      }
      .health-pill {
        padding: 6px;
      }
    }
  `]
})
export class HeaderComponent implements OnInit, OnDestroy {
  @Output() toggleSidebar = new EventEmitter<void>();

  private readonly router = inject(Router);
  private readonly api = inject(JanusApiService);
  readonly session = inject(SessionService);
  private readonly notification = inject(NotificationService);

  readonly currentPageTitle = signal('Tableau de bord');
  readonly currentPageSubtitle = signal('Vue d\'ensemble de l\'exploitation billettique');
  readonly apiStatus = signal('checking...');
  readonly showModal = signal(false);
  readonly sessionError = signal<string | null>(null);

  tempTerminalToken = '';
  tempAdminToken = '';
  selectedRole: UserRole = 'ANONYMOUS';
  roleExplicitlySelected = false;

  private healthInterval: ReturnType<typeof setInterval> | null = null;
  private routerSub?: Subscription;

  ngOnInit(): void {
    this.updateRouteTitles(this.router.url);
    this.routerSub = this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe((e: NavigationEnd) => {
      this.updateRouteTitles(e.urlAfterRedirects);
    });

    this.checkHealth();
    this.healthInterval = setInterval(() => this.checkHealth(), 15000);

    this.tempTerminalToken = this.session.terminalToken() || '';
    this.tempAdminToken = this.session.adminToken() || '';
    this.selectedRole = this.session.activeRole();
  }

  ngOnDestroy(): void {
    if (this.healthInterval) clearInterval(this.healthInterval);
    this.routerSub?.unsubscribe();
  }

  private updateRouteTitles(url: string): void {
    if (url.includes('/dashboard')) {
      this.currentPageTitle.set('Tableau de bord');
      this.currentPageSubtitle.set('Indicateurs de billettique, statistiques et santé système');
    } else if (url.includes('/cards')) {
      this.currentPageTitle.set('Gestion des cartes');
      this.currentPageSubtitle.set('Inventaire des supports RFID, suivi des soldes et historique de débit');
    } else if (url.includes('/simulator')) {
      this.currentPageTitle.set('Simulateur de valideur RFID');
      this.currentPageSubtitle.set('Banc d\'essai de validation sans contact (NFC / ISO 14443)');
    } else if (url.includes('/audit')) {
      this.currentPageTitle.set('Audit & Sécurité');
      this.currentPageSubtitle.set('Journal de traçabilité des modifications administratives');
    }
  }

  checkHealth(): void {
    this.api.getHealth().subscribe({
      next: (res) => this.apiStatus.set(res.status),
      error: () => this.apiStatus.set('unreachable')
    });
  }

  toggleModal(): void {
    this.tempTerminalToken = this.session.terminalToken() || '';
    this.tempAdminToken = this.session.adminToken() || '';
    this.selectedRole = this.session.activeRole();
    this.roleExplicitlySelected = false;
    this.sessionError.set(null);
    this.showModal.update(v => !v);
  }

  selectRole(role: UserRole): void {
    this.selectedRole = role;
    this.roleExplicitlySelected = true;
    this.sessionError.set(null);
  }

  onTerminalTokenInput(value: string): void {
    this.tempTerminalToken = value;
    this.sessionError.set(null);
    if (!this.roleExplicitlySelected && this.selectedRole === 'ANONYMOUS' && value.trim()) {
      this.selectedRole = 'TERMINAL';
    }
  }

  onAdminTokenInput(value: string): void {
    this.tempAdminToken = value;
    this.sessionError.set(null);
    if (!this.roleExplicitlySelected && (this.selectedRole === 'ANONYMOUS' || this.selectedRole === 'TERMINAL') && value.trim()) {
      this.selectedRole = 'ADMIN';
    }
  }

  clearTokens(): void {
    this.tempTerminalToken = '';
    this.tempAdminToken = '';
    this.selectedRole = 'ANONYMOUS';
    this.roleExplicitlySelected = false;
    this.sessionError.set(null);
    this.session.clearTokens();
    this.showModal.set(false);
    this.notification.warning('Session effacée', 'Tous les jetons de session ont été purgés.');
  }

  applySession(): void {
    const trimmedTerminal = this.tempTerminalToken.trim();
    const trimmedAdmin = this.tempAdminToken.trim();

    let targetRole: UserRole = this.selectedRole;
    if (!this.roleExplicitlySelected && targetRole === 'ANONYMOUS') {
      if (trimmedAdmin) {
        targetRole = 'ADMIN';
      } else if (trimmedTerminal) {
        targetRole = 'TERMINAL';
      }
    }

    if (targetRole === 'ADMIN' && !trimmedAdmin) {
      const msg = 'Le rôle Administrateur requiert la saisie d\'un jeton Administrateur valide.';
      this.sessionError.set(msg);
      this.notification.warning('Jeton requis', msg);
      return;
    }

    if (targetRole === 'TERMINAL' && !trimmedTerminal) {
      const msg = 'Le rôle Opérateur Borne requiert la saisie d\'un jeton Opérateur Borne valide.';
      this.sessionError.set(msg);
      this.notification.warning('Jeton requis', msg);
      return;
    }

    this.selectedRole = targetRole;
    this.sessionError.set(null);
    this.session.configureSession({
      terminalToken: trimmedTerminal || null,
      adminToken: trimmedAdmin || null,
      role: targetRole
    });
    this.showModal.set(false);
    this.notification.success('Session configurée', `Profil actif : ${targetRole}`);
  }
}
