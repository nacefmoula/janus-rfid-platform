import { Component, EventEmitter, Input, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { SessionService } from '../../../core/services/session.service';

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterModule],
  template: `
    <aside class="sidebar" [class.open]="isOpen">
      <!-- En-tête de la barre latérale -->
      <div class="sidebar-brand">
        <div class="brand-badge" aria-hidden="true">
          <svg viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M4.9 19.1C1 15.2 1 8.8 4.9 4.9"/>
            <path d="M7.8 16.2c-2.3-2.3-2.3-6.1 0-8.5"/>
            <circle cx="12" cy="12" r="2"/>
            <path d="M16.2 7.8c2.3 2.3 2.3 6.1 0 8.5"/>
            <path d="M19.1 4.9C23 8.8 23 15.2 19.1 19.1"/>
          </svg>
        </div>
        <div class="brand-info">
          <span class="brand-name">Janus RFID</span>
          <span class="brand-tag">Système Billettique</span>
        </div>
        <button
          type="button"
          class="sidebar-close-btn"
          (click)="closeSidebar.emit()"
          aria-label="Fermer le menu de navigation">
          ✕
        </button>
      </div>

      <!-- Navigation principale -->
      <nav class="sidebar-nav" aria-label="Navigation principale">
        <div class="nav-section-label">EXPLOITATION</div>

        <a routerLink="/dashboard" routerLinkActive="active" class="nav-link" (click)="onNavClick()">
          <span class="nav-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <rect x="3" y="3" width="7" height="9"/>
              <rect x="14" y="3" width="7" height="5"/>
              <rect x="14" y="12" width="7" height="9"/>
              <rect x="3" y="16" width="7" height="5"/>
            </svg>
          </span>
          <span class="nav-text">Tableau de bord</span>
        </a>

        <a routerLink="/cards" routerLinkActive="active" class="nav-link" (click)="onNavClick()">
          <span class="nav-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <rect x="2" y="5" width="20" height="14" rx="2"/>
              <line x1="2" y1="10" x2="22" y2="10"/>
            </svg>
          </span>
          <span class="nav-text">Gestion des cartes</span>
        </a>

        <a routerLink="/simulator" routerLinkActive="active" class="nav-link" (click)="onNavClick()">
          <span class="nav-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="M5 12.55a11 11 0 0 1 14.08 0"/>
              <path d="M1.42 9a16 16 0 0 1 21.16 0"/>
              <path d="M8.53 16.11a6 6 0 0 1 6.95 0"/>
              <line x1="12" y1="20" x2="12.01" y2="20"/>
            </svg>
          </span>
          <span class="nav-text">Simulateur de valideur</span>
        </a>

        <div class="nav-section-label">GOUVERNANCE</div>

        <a routerLink="/audit" routerLinkActive="active" class="nav-link" (click)="onNavClick()">
          <span class="nav-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/>
              <polyline points="9 12 11 14 15 10"/>
            </svg>
          </span>
          <span class="nav-text">Audit & Sécurité</span>
        </a>

        <div class="nav-section-label">SUPERVISION TECHNIQUE</div>

        <a href="http://localhost:3000" target="_blank" rel="noopener noreferrer" class="nav-link external">
          <span class="nav-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <line x1="18" y1="20" x2="18" y2="10"/>
              <line x1="12" y1="20" x2="12" y2="4"/>
              <line x1="6" y1="20" x2="6" y2="14"/>
            </svg>
          </span>
          <span class="nav-text">Grafana (Port 3000)</span>
          <span class="external-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>
              <polyline points="15 3 21 3 21 9"/>
              <line x1="10" y1="14" x2="21" y2="3"/>
            </svg>
          </span>
        </a>

        <a href="http://localhost:9090" target="_blank" rel="noopener noreferrer" class="nav-link external">
          <span class="nav-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/>
            </svg>
          </span>
          <span class="nav-text">Prometheus (Port 9090)</span>
          <span class="external-icon" aria-hidden="true">
            <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2">
              <path d="M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6"/>
              <polyline points="15 3 21 3 21 9"/>
              <line x1="10" y1="14" x2="21" y2="3"/>
            </svg>
          </span>
        </a>
      </nav>

      <!-- Pied de barre latérale : profil actif -->
      <div class="sidebar-footer">
        <div class="session-card">
          <div class="session-label">PROFIL ACTIF</div>
          <div class="session-role" [ngClass]="'role-' + session.activeRole().toLowerCase()">
            <span class="role-dot" aria-hidden="true"></span>
            <span class="role-title">
              @switch (session.activeRole()) {
                @case ('ADMIN') { Administrateur }
                @case ('TERMINAL') { Opérateur Borne }
                @default { Visiteur (Lecture) }
              }
            </span>
          </div>
        </div>
      </div>
    </aside>
  `,
  styles: [`
    .sidebar {
      width: 260px;
      background-color: var(--bg-sidebar);
      color: #94A3B8;
      display: flex;
      flex-direction: column;
      flex-shrink: 0;
      border-right: 1px solid var(--bg-sidebar-border);
      min-height: 100vh;
      transition: transform 0.2s ease-in-out;
    }

    .sidebar-brand {
      height: 64px;
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 0 18px;
      border-bottom: 1px solid var(--bg-sidebar-border);
      position: relative;
    }

    .brand-badge {
      width: 34px;
      height: 34px;
      background-color: #1E293B;
      border: 1px solid #334155;
      color: #38BDF8;
      display: flex;
      align-items: center;
      justify-content: center;
      border-radius: 6px;
      flex-shrink: 0;
    }

    .brand-info {
      display: flex;
      flex-direction: column;
      min-width: 0;
    }

    .brand-name {
      font-size: 0.9375rem;
      font-weight: 700;
      color: #F8FAFC;
      letter-spacing: -0.01em;
      line-height: 1.2;
    }

    .brand-tag {
      font-size: 0.6875rem;
      color: #64748B;
      font-weight: 500;
      letter-spacing: 0.02em;
    }

    .sidebar-close-btn {
      display: none;
      margin-left: auto;
      background: transparent;
      border: none;
      color: #94A3B8;
      font-size: 1.1rem;
      cursor: pointer;
      padding: 6px;
      border-radius: 4px;
    }
    .sidebar-close-btn:hover {
      color: #FFFFFF;
      background-color: #1E293B;
    }

    .sidebar-nav {
      flex: 1;
      padding: 16px 10px;
      display: flex;
      flex-direction: column;
      gap: 3px;
      overflow-y: auto;
    }

    .nav-section-label {
      font-size: 0.625rem;
      font-weight: 700;
      color: #64748B;
      letter-spacing: 0.08em;
      padding: 14px 10px 6px 10px;
    }

    .nav-link {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 8px 12px;
      color: #94A3B8;
      border-radius: 6px;
      font-size: 0.8125rem;
      font-weight: 500;
      text-decoration: none;
      transition: all 0.15s ease-in-out;
      border-left: 3px solid transparent;
    }

    .nav-link:hover {
      color: #F8FAFC;
      background-color: var(--bg-sidebar-hover);
      text-decoration: none;
    }

    .nav-link.active {
      color: #FFFFFF;
      background-color: #1E293B;
      border-left-color: #38BDF8;
      font-weight: 600;
    }

    .nav-icon {
      display: flex;
      align-items: center;
      justify-content: center;
      color: currentColor;
      flex-shrink: 0;
    }

    .external {
      color: #64748B;
    }
    .external:hover {
      color: #CBD5E1;
    }

    .external-icon {
      margin-left: auto;
      display: flex;
      align-items: center;
      opacity: 0.6;
    }

    .sidebar-footer {
      padding: 14px;
      border-top: 1px solid var(--bg-sidebar-border);
      background-color: #0B1120;
    }

    .session-card {
      background-color: #1E293B;
      border-radius: 6px;
      padding: 10px 12px;
      border: 1px solid #334155;
    }

    .session-label {
      font-size: 0.625rem;
      font-weight: 700;
      letter-spacing: 0.06em;
      color: #64748B;
      margin-bottom: 4px;
    }

    .session-role {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 0.8125rem;
      font-weight: 600;
    }

    .role-dot {
      width: 7px;
      height: 7px;
      border-radius: 50%;
      flex-shrink: 0;
    }

    .role-admin { color: #FCA5A5; }
    .role-admin .role-dot { background-color: #EF4444; }

    .role-terminal { color: #BAE6FD; }
    .role-terminal .role-dot { background-color: #38BDF8; }

    .role-anonymous { color: #CBD5E1; }
    .role-anonymous .role-dot { background-color: #94A3B8; }

    /* Règles responsive pour petits écrans */
    @media (max-width: 1023px) {
      .sidebar-close-btn {
        display: block;
      }
    }
  `]
})
export class SidebarComponent {
  readonly session = inject(SessionService);

  @Input() isOpen = false;
  @Output() closeSidebar = new EventEmitter<void>();

  onNavClick(): void {
    this.closeSidebar.emit();
  }
}
