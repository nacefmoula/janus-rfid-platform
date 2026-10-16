import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';
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
        <h1 class="page-title">{{ currentPageTitle() }}</h1>
        <span class="page-subtitle">{{ currentPageSubtitle() }}</span>
      </div>

      <div class="header-right">
        <!-- Indicateur de santé de l'API Janus -->
        <div class="health-pill" [class.healthy]="apiStatus() === 'healthy'" [class.unhealthy]="apiStatus() !== 'healthy'">
          <span class="health-dot" aria-hidden="true"></span>
          <span class="health-label">API Janus : <strong>{{ apiStatus() === 'healthy' ? 'En ligne (Port 5080)' : 'Non joignable' }}</strong></span>
        </div>

        <!-- Bouton de gestion de la session en mémoire -->
        <button type="button" class="btn btn-secondary session-trigger" (click)="toggleModal()" title="Paramétrer les jetons de démonstration">
          <span class="icon">🔑</span>
          <span>Session & Rôles</span>
        </button>
      </div>
    </header>

    <!-- Modal des jetons de test en mémoire vive -->
    @if (showModal()) {
      <div class="modal-backdrop" (click)="toggleModal()">
        <div class="modal-dialog" (click)="$event.stopPropagation()">
          <div class="modal-header">
            <h3>🔑 Configuration de Session de Test</h3>
            <button type="button" class="close-btn" (click)="toggleModal()" aria-label="Fermer">✕</button>
          </div>

          <div class="modal-body">
            <div class="notice-box">
              <span class="notice-icon">🔒</span>
              <p>
                <strong>Sécurité & Confidentialité :</strong> Aucun jeton n'est stocké dans le navigateur (pas de <code>localStorage</code>).
                Les identifiants saisis ici restent en mémoire vive pour tester les contrôles d'accès réels de l'API Janus.
              </p>
            </div>

            <div class="form-group">
              <label class="form-label" for="terminalToken">Jeton Opérateur Borne (TerminalAuth:DemoToken)</label>
              <input
                id="terminalToken"
                type="password"
                class="form-input"
                placeholder="Ex: dev-terminal-token-change-me"
                [(ngModel)]="tempTerminalToken"
              />
              <span class="field-hint">Autorise la validation des titres et la consultation des cartes.</span>
            </div>

            <div class="form-group">
              <label class="form-label" for="adminToken">Jeton Administrateur (AdminAuth:DemoToken)</label>
              <input
                id="adminToken"
                type="password"
                class="form-input"
                placeholder="Ex: dev-admin-token-change-me"
                [(ngModel)]="tempAdminToken"
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
                  (click)="selectedRole = 'ANONYMOUS'">
                  <strong>Visiteur</strong>
                  <small>Lecture publique seule</small>
                </button>
                <button
                  type="button"
                  class="role-card"
                  [class.active]="selectedRole === 'TERMINAL'"
                  (click)="selectedRole = 'TERMINAL'">
                  <strong>Opérateur Borne</strong>
                  <small>Validation de trajets</small>
                </button>
                <button
                  type="button"
                  class="role-card"
                  [class.active]="selectedRole === 'ADMIN'"
                  (click)="selectedRole = 'ADMIN'">
                  <strong>Administrateur</strong>
                  <small>Gestion complète & Audit</small>
                </button>
              </div>
            </div>

            <div class="helpers-row">
              <button type="button" class="btn btn-secondary" (click)="fillDevTokens()">
                ⚡ Charger tokens démo locaux
              </button>
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
      height: 70px;
      background-color: var(--bg-surface);
      border-bottom: 1px solid var(--border-light);
      padding: 0 32px;
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
      flex-direction: column;
    }
    .page-title {
      font-size: 1.25rem;
      font-weight: 800;
      color: var(--color-text-main);
      line-height: 1.2;
    }
    .page-subtitle {
      font-size: 0.8125rem;
      color: var(--color-text-muted);
    }

    .header-right {
      display: flex;
      align-items: center;
      gap: 16px;
    }

    .health-pill {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 6px 14px;
      border-radius: var(--radius-full);
      font-size: 0.8125rem;
      background-color: var(--bg-surface-subtle);
      border: 1px solid var(--border-light);
    }
    .health-dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
    }
    .health-pill.healthy .health-dot {
      background-color: var(--color-success);
      box-shadow: 0 0 6px var(--color-success);
    }
    .health-pill.unhealthy .health-dot {
      background-color: var(--color-danger);
      box-shadow: 0 0 6px var(--color-danger);
    }

    .session-trigger {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 0.8125rem;
    }

    /* Modal */
    .modal-backdrop {
      position: fixed;
      inset: 0;
      background: rgba(9, 30, 66, 0.54);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 1100;
      backdrop-filter: blur(2px);
    }

    .modal-dialog {
      background: var(--bg-surface);
      border-radius: var(--radius-lg);
      width: 100%;
      max-width: 520px;
      box-shadow: var(--shadow-lg);
      overflow: hidden;
      animation: modalFadeIn 0.15s ease-out;
    }
    @keyframes modalFadeIn {
      from { transform: scale(0.96); opacity: 0; }
      to { transform: scale(1); opacity: 1; }
    }

    .modal-header {
      padding: 16px 24px;
      border-bottom: 1px solid var(--border-light);
      display: flex;
      align-items: center;
      justify-content: space-between;
    }
    .modal-header h3 {
      font-size: 1.1rem;
      font-weight: 700;
      color: var(--color-text-main);
    }
    .close-btn {
      background: transparent;
      border: none;
      font-size: 1.25rem;
      color: var(--color-text-dim);
      cursor: pointer;
    }

    .modal-body {
      padding: 24px;
      display: flex;
      flex-direction: column;
      gap: 16px;
    }

    .notice-box {
      background-color: var(--color-info-bg);
      border: 1px solid var(--color-info-border);
      border-radius: var(--radius-md);
      padding: 12px 14px;
      display: flex;
      gap: 10px;
      color: var(--color-info-text);
      font-size: 0.8125rem;
      line-height: 1.4;
    }
    .notice-icon {
      font-size: 1.2rem;
      flex-shrink: 0;
    }

    .form-group {
      display: flex;
      flex-direction: column;
      gap: 4px;
    }
    .field-hint {
      font-size: 0.75rem;
      color: var(--color-text-dim);
    }

    .role-grid {
      display: grid;
      grid-template-columns: repeat(3, 1fr);
      gap: 8px;
    }
    .role-card {
      padding: 10px 8px;
      background: var(--bg-surface-subtle);
      border: 1px solid var(--border-light);
      border-radius: var(--radius-md);
      cursor: pointer;
      text-align: center;
      transition: all 0.15s ease;
    }
    .role-card strong {
      display: block;
      font-size: 0.8125rem;
      color: var(--color-text-main);
    }
    .role-card small {
      display: block;
      font-size: 0.7rem;
      color: var(--color-text-muted);
    }
    .role-card.active {
      background-color: var(--color-primary-light);
      border-color: var(--color-primary);
    }
    .role-card.active strong {
      color: var(--color-primary);
    }

    .helpers-row {
      display: flex;
      justify-content: space-between;
      align-items: center;
      margin-top: 8px;
    }
    .btn-text-danger {
      background: none;
      border: none;
      color: var(--color-danger);
      font-size: 0.8125rem;
      font-weight: 600;
      cursor: pointer;
    }

    .modal-footer {
      padding: 16px 24px;
      background-color: var(--bg-surface-subtle);
      border-top: 1px solid var(--border-light);
      display: flex;
      justify-content: flex-end;
      gap: 12px;
    }
  `]
})
export class HeaderComponent implements OnInit, OnDestroy {
  private readonly router = inject(Router);
  private readonly api = inject(JanusApiService);
  readonly session = inject(SessionService);
  private readonly notification = inject(NotificationService);

  readonly currentPageTitle = signal('Tableau de bord');
  readonly currentPageSubtitle = signal('Vue d\'ensemble de l\'exploitation billettique');
  readonly apiStatus = signal('checking...');
  readonly showModal = signal(false);

  tempTerminalToken = '';
  tempAdminToken = '';
  selectedRole: UserRole = 'ANONYMOUS';

  private healthInterval: any = null;

  ngOnInit(): void {
    this.updateRouteTitles(this.router.url);
    this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe((e: any) => {
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
    this.showModal.update(v => !v);
  }

  fillDevTokens(): void {
    this.tempTerminalToken = 'dev-terminal-token-change-me';
    this.tempAdminToken = 'dev-admin-token-change-me';
    this.selectedRole = 'TERMINAL';
    this.notification.info('Jetons pré-remplis', 'Identifiants de développement injectés en mémoire vive.');
  }

  clearTokens(): void {
    this.tempTerminalToken = '';
    this.tempAdminToken = '';
    this.selectedRole = 'ANONYMOUS';
    this.session.clearTokens();
    this.notification.warning('Session effacée', 'Tous les jetons de session ont été purgés.');
  }

  applySession(): void {
    this.session.setTerminalToken(this.tempTerminalToken);
    this.session.setAdminToken(this.tempAdminToken);
    this.session.setActiveRole(this.selectedRole);
    this.showModal.set(false);
    this.notification.success('Session configurée', `Profil actif : ${this.selectedRole}`);
  }
}
