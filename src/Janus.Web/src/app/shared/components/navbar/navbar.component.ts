import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { SessionService, UserRole } from '../../../core/services/session.service';
import { JanusApiService } from '../../../core/services/api.service';
import { NotificationService } from '../../../core/services/notification.service';

@Component({
  selector: 'app-navbar',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule],
  template: `
    <header class="header">
      <div class="header-container">
        <!-- Logo & Identité -->
        <div class="brand">
          <a routerLink="/" class="brand-link">
            <span class="brand-icon">⚡</span>
            <div class="brand-text">
              <span class="brand-title">JANUS RFID</span>
              <span class="brand-subtitle">Démonstrateur PFE Billettique</span>
            </div>
          </a>
        </div>

        <!-- Navigation principale -->
        <nav class="nav">
          <a routerLink="/simulator" routerLinkActive="active" class="nav-item">
            <span class="nav-icon">🎛️</span>
            <span>Simulateur RFID</span>
          </a>
          <a routerLink="/dashboard" routerLinkActive="active" class="nav-item">
            <span class="nav-icon">📊</span>
            <span>Dashboard</span>
          </a>
          <a routerLink="/cards" routerLinkActive="active" class="nav-item">
            <span class="nav-icon">💳</span>
            <span>Gestion des cartes</span>
          </a>
          <a routerLink="/audit" routerLinkActive="active" class="nav-item">
            <span class="nav-icon">🛡️</span>
            <span>Audit & Sécurité</span>
          </a>
        </nav>

        <!-- Statut Santé API + Gestion de Session -->
        <div class="actions">
          <div class="health-status" [title]="'API Janus: ' + apiHealthStatus()">
            <span class="health-indicator" [ngClass]="apiHealthStatus() === 'healthy' ? 'online' : 'offline'"></span>
            <span class="health-text">API : {{ apiHealthStatus() }}</span>
          </div>

          <button class="session-btn" (click)="toggleSessionModal()" [title]="'Gérer les jetons de test'">
            <span class="role-badge" [ngClass]="'role-' + session.activeRole().toLowerCase()">
              {{ session.activeRole() }}
            </span>
            <span class="gear-icon">⚙️</span>
          </button>
        </div>
      </div>
    </header>

    <!-- Modal de configuration de session en mémoire volatile -->
    @if (showSessionModal()) {
      <div class="modal-backdrop" (click)="toggleSessionModal()">
        <div class="modal-card" (click)="$event.stopPropagation()">
          <div class="modal-header">
            <h3>🔑 Session & Jetons de Démonstration</h3>
            <button class="close-btn" (click)="toggleSessionModal()">✕</button>
          </div>

          <div class="modal-body">
            <p class="modal-disclaimer">
              🔒 <strong>Sécurité DevSecOps :</strong> Aucun jeton n'est stocké dans le bundle ni dans le <code>localStorage</code>.
              Ces identifiants sont conservés strictement en mémoire vive (RAM) durant votre visite.
            </p>

            <div class="form-group">
              <label for="terminalToken">Jeton Borne RFID (TerminalAuth:DemoToken)</label>
              <input
                id="terminalToken"
                type="password"
                [(ngModel)]="inputTerminalToken"
                placeholder="Saisir le jeton opérateur borne (depuis .env)"
                class="form-control"
              />
              <span class="help-text">Requis pour <code>POST /api/validations</code> et <code>GET /api/cards/&#123;uid&#125;</code></span>
            </div>

            <div class="form-group">
              <label for="adminToken">Jeton Administrateur (AdminAuth:DemoToken)</label>
              <input
                id="adminToken"
                type="password"
                [(ngModel)]="inputAdminToken"
                placeholder="Saisir le jeton administrateur (depuis .env)"
                class="form-control"
              />
              <span class="help-text">Requis pour <code>PUT /api/cards/&#123;uid&#125;</code> et <code>GET /api/cards/&#123;uid&#125;/audit</code></span>
            </div>

            <div class="form-group">
              <label>Rôle actif simulé</label>
              <div class="role-selector">
                <button
                  type="button"
                  class="role-opt"
                  [class.selected]="selectedRole === 'ANONYMOUS'"
                  (click)="selectedRole = 'ANONYMOUS'">
                  Visiteur (Anonyme)
                </button>
                <button
                  type="button"
                  class="role-opt"
                  [class.selected]="selectedRole === 'TERMINAL'"
                  (click)="selectedRole = 'TERMINAL'">
                  Opérateur Borne
                </button>
                <button
                  type="button"
                  class="role-opt"
                  [class.selected]="selectedRole === 'ADMIN'"
                  (click)="selectedRole = 'ADMIN'">
                  Administrateur
                </button>
              </div>
            </div>

            <div class="quick-helpers">
              <button type="button" class="btn-danger-outline" (click)="clearSession()">
                🗑 Effacer la session
              </button>
            </div>
          </div>

          <div class="modal-footer">
            <button type="button" class="btn-primary" (click)="saveSession()">
              Valider la configuration
            </button>
          </div>
        </div>
      </div>
    }
  `,
  styles: [`
    .header {
      background: #0f172a;
      border-bottom: 1px solid #1e293b;
      position: sticky;
      top: 0;
      z-index: 1000;
    }
    .header-container {
      max-width: 1400px;
      margin: 0 auto;
      padding: 0 24px;
      height: 64px;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 20px;
    }
    .brand-link {
      display: flex;
      align-items: center;
      gap: 12px;
      text-decoration: none;
      color: inherit;
    }
    .brand-icon {
      font-size: 1.5rem;
      background: linear-gradient(135deg, #3b82f6, #06b6d4);
      -webkit-background-clip: text;
      -webkit-text-fill-color: transparent;
    }
    .brand-title {
      font-size: 1.1rem;
      font-weight: 800;
      letter-spacing: 0.05em;
      color: #f8fafc;
      display: block;
    }
    .brand-subtitle {
      font-size: 0.7rem;
      color: #64748b;
      display: block;
    }
    .nav {
      display: flex;
      align-items: center;
      gap: 8px;
    }
    .nav-item {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 8px 14px;
      border-radius: 6px;
      text-decoration: none;
      color: #94a3b8;
      font-size: 0.875rem;
      font-weight: 500;
      transition: all 0.15s ease-in-out;
    }
    .nav-item:hover {
      color: #f8fafc;
      background: #1e293b;
    }
    .nav-item.active {
      color: #38bdf8;
      background: rgba(56, 189, 248, 0.1);
      border: 1px solid rgba(56, 189, 248, 0.2);
    }
    .actions {
      display: flex;
      align-items: center;
      gap: 16px;
    }
    .health-status {
      display: flex;
      align-items: center;
      gap: 6px;
      font-size: 0.75rem;
      color: #cbd5e1;
      padding: 4px 10px;
      background: #1e293b;
      border-radius: 20px;
    }
    .health-indicator {
      width: 8px;
      height: 8px;
      border-radius: 50%;
    }
    .health-indicator.online {
      background: #10b981;
      box-shadow: 0 0 8px #10b981;
    }
    .health-indicator.offline {
      background: #ef4444;
      box-shadow: 0 0 8px #ef4444;
    }
    .session-btn {
      display: flex;
      align-items: center;
      gap: 8px;
      padding: 5px 10px;
      border-radius: 6px;
      background: #1e293b;
      border: 1px solid #334155;
      color: #f8fafc;
      cursor: pointer;
      font-size: 0.8rem;
    }
    .session-btn:hover {
      background: #334155;
    }
    .role-badge {
      font-weight: 700;
      font-size: 0.7rem;
      padding: 2px 6px;
      border-radius: 4px;
      text-transform: uppercase;
    }
    .role-anonymous { background: #475569; color: #f8fafc; }
    .role-terminal { background: #0284c7; color: #f8fafc; }
    .role-admin { background: #b91c1c; color: #f8fafc; }

    /* Modal */
    .modal-backdrop {
      position: fixed;
      inset: 0;
      background: rgba(0, 0, 0, 0.7);
      display: flex;
      align-items: center;
      justify-content: center;
      z-index: 2000;
      backdrop-filter: blur(4px);
    }
    .modal-card {
      background: #1e293b;
      border: 1px solid #334155;
      border-radius: 12px;
      width: 100%;
      max-width: 540px;
      box-shadow: 0 25px 50px -12px rgba(0, 0, 0, 0.5);
      overflow: hidden;
    }
    .modal-header {
      display: flex;
      align-items: center;
      justify-content: space-between;
      padding: 16px 20px;
      border-bottom: 1px solid #334155;
    }
    .modal-header h3 {
      margin: 0;
      font-size: 1.1rem;
      color: #f8fafc;
    }
    .close-btn {
      background: none;
      border: none;
      color: #94a3b8;
      font-size: 1.2rem;
      cursor: pointer;
    }
    .modal-body {
      padding: 20px;
      display: flex;
      flex-direction: column;
      gap: 16px;
    }
    .modal-disclaimer {
      font-size: 0.8rem;
      color: #94a3b8;
      background: #0f172a;
      padding: 10px 12px;
      border-radius: 6px;
      border-left: 3px solid #38bdf8;
      margin: 0;
    }
    .form-group {
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .form-group label {
      font-size: 0.85rem;
      font-weight: 600;
      color: #cbd5e1;
    }
    .form-control {
      background: #0f172a;
      border: 1px solid #334155;
      color: #f8fafc;
      padding: 8px 12px;
      border-radius: 6px;
      font-size: 0.875rem;
      outline: none;
    }
    .form-control:focus {
      border-color: #38bdf8;
    }
    .help-text {
      font-size: 0.75rem;
      color: #64748b;
    }
    .role-selector {
      display: flex;
      gap: 8px;
    }
    .role-opt {
      flex: 1;
      padding: 8px;
      border: 1px solid #334155;
      background: #0f172a;
      color: #94a3b8;
      border-radius: 6px;
      font-size: 0.8rem;
      font-weight: 600;
      cursor: pointer;
      transition: all 0.15s ease;
    }
    .role-opt.selected {
      background: #0284c7;
      color: #ffffff;
      border-color: #38bdf8;
    }
    .quick-helpers {
      display: flex;
      gap: 10px;
      margin-top: 8px;
    }
    .btn-secondary {
      flex: 1;
      background: #334155;
      border: 1px solid #475569;
      color: #f8fafc;
      padding: 8px 12px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 0.8rem;
    }
    .btn-danger-outline {
      background: transparent;
      border: 1px solid #ef4444;
      color: #ef4444;
      padding: 8px 12px;
      border-radius: 6px;
      cursor: pointer;
      font-size: 0.8rem;
    }
    .modal-footer {
      padding: 14px 20px;
      background: #0f172a;
      border-top: 1px solid #334155;
      display: flex;
      justify-content: flex-end;
    }
    .btn-primary {
      background: #0284c7;
      border: none;
      color: #ffffff;
      padding: 8px 18px;
      border-radius: 6px;
      font-weight: 600;
      font-size: 0.875rem;
      cursor: pointer;
    }
    .btn-primary:hover {
      background: #0369a1;
    }
  `]
})
export class NavbarComponent implements OnInit, OnDestroy {
  readonly session = inject(SessionService);
  private readonly api = inject(JanusApiService);
  private readonly notification = inject(NotificationService);

  readonly showSessionModal = signal(false);
  readonly apiHealthStatus = signal('checking...');
  private healthInterval: any = null;

  inputTerminalToken = '';
  inputAdminToken = '';
  selectedRole: UserRole = 'ANONYMOUS';

  ngOnInit(): void {
    this.refreshHealth();
    this.healthInterval = setInterval(() => this.refreshHealth(), 15000);

    // Initialise les inputs avec les valeurs courantes
    this.inputTerminalToken = this.session.terminalToken() || '';
    this.inputAdminToken = this.session.adminToken() || '';
    this.selectedRole = this.session.activeRole();
  }

  ngOnDestroy(): void {
    if (this.healthInterval) {
      clearInterval(this.healthInterval);
    }
  }

  refreshHealth(): void {
    this.api.getHealth().subscribe({
      next: (res) => this.apiHealthStatus.set(res.status),
      error: () => this.apiHealthStatus.set('unreachable')
    });
  }

  toggleSessionModal(): void {
    this.inputTerminalToken = this.session.terminalToken() || '';
    this.inputAdminToken = this.session.adminToken() || '';
    this.selectedRole = this.session.activeRole();
    this.showSessionModal.update(v => !v);
  }

  clearSession(): void {
    this.inputTerminalToken = '';
    this.inputAdminToken = '';
    this.selectedRole = 'ANONYMOUS';
    this.session.clearTokens();
    this.notification.warning('Session réinitialisée', 'Tous les jetons ont été purgés de la mémoire.');
  }

  saveSession(): void {
    this.session.setTerminalToken(this.inputTerminalToken);
    this.session.setAdminToken(this.inputAdminToken);
    this.session.setActiveRole(this.selectedRole);
    this.showSessionModal.set(false);
    this.notification.success('Session configurée', `Rôle actif : ${this.selectedRole}`);
  }
}
