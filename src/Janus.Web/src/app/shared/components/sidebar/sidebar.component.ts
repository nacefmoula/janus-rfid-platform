import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { SessionService } from '../../../core/services/session.service';

@Component({
  selector: 'app-sidebar',
  standalone: true,
  imports: [CommonModule, RouterModule],
  template: `
    <aside class="sidebar">
      <!-- En-tête de la barre latérale -->
      <div class="sidebar-brand">
        <div class="brand-badge">⚡</div>
        <div class="brand-info">
          <span class="brand-name">Janus RFID</span>
          <span class="brand-tag">Billettique & Transport</span>
        </div>
      </div>

      <!-- Navigation principale -->
      <nav class="sidebar-nav" aria-label="Navigation principale">
        <div class="nav-section-label">EXPLOITATION</div>

        <a routerLink="/dashboard" routerLinkActive="active" class="nav-link">
          <span class="nav-icon" aria-hidden="true">📊</span>
          <span class="nav-text">Tableau de bord</span>
        </a>

        <a routerLink="/cards" routerLinkActive="active" class="nav-link">
          <span class="nav-icon" aria-hidden="true">💳</span>
          <span class="nav-text">Gestion des cartes</span>
        </a>

        <a routerLink="/simulator" routerLinkActive="active" class="nav-link">
          <span class="nav-icon" aria-hidden="true">🎛️</span>
          <span class="nav-text">Simulateur de valideur</span>
        </a>

        <div class="nav-section-label">GOUVERNANCE</div>

        <a routerLink="/audit" routerLinkActive="active" class="nav-link">
          <span class="nav-icon" aria-hidden="true">🛡️</span>
          <span class="nav-text">Audit & Sécurité</span>
        </a>

        <div class="nav-section-label">OBSERVABILITÉ EXTERNE</div>

        <a href="http://localhost:3000" target="_blank" rel="noopener noreferrer" class="nav-link external">
          <span class="nav-icon" aria-hidden="true">📈</span>
          <span class="nav-text">Grafana (Port 3000)</span>
          <span class="external-icon">↗</span>
        </a>

        <a href="http://localhost:9090" target="_blank" rel="noopener noreferrer" class="nav-link external">
          <span class="nav-icon" aria-hidden="true">🔥</span>
          <span class="nav-text">Prometheus (Port 9090)</span>
          <span class="external-icon">↗</span>
        </a>
      </nav>

      <!-- Pied de barre latérale : profil actif -->
      <div class="sidebar-footer">
        <div class="session-card">
          <div class="session-label">PROFIL ACTIF</div>
          <div class="session-role" [ngClass]="'role-' + session.activeRole().toLowerCase()">
            <span class="role-dot"></span>
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
      border-right: 1px solid #1E293B;
      min-height: 100vh;
    }

    .sidebar-brand {
      height: 70px;
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 0 20px;
      border-bottom: 1px solid #1E293B;
    }

    .brand-badge {
      width: 36px;
      height: 36px;
      background: linear-gradient(135deg, #2563EB, #06B6D4);
      color: #FFFFFF;
      display: flex;
      align-items: center;
      justify-content: center;
      border-radius: 8px;
      font-size: 1.1rem;
      font-weight: bold;
    }

    .brand-info {
      display: flex;
      flex-direction: column;
    }

    .brand-name {
      font-size: 1.05rem;
      font-weight: 800;
      color: #F8FAFC;
      letter-spacing: 0.02em;
    }

    .brand-tag {
      font-size: 0.7rem;
      color: #64748B;
      font-weight: 500;
    }

    .sidebar-nav {
      flex: 1;
      padding: 20px 12px;
      display: flex;
      flex-direction: column;
      gap: 4px;
    }

    .nav-section-label {
      font-size: 0.65rem;
      font-weight: 700;
      color: #475569;
      letter-spacing: 0.08em;
      padding: 12px 12px 6px 12px;
    }

    .nav-link {
      display: flex;
      align-items: center;
      gap: 12px;
      padding: 10px 14px;
      color: #94A3B8;
      border-radius: 8px;
      font-size: 0.875rem;
      font-weight: 600;
      text-decoration: none;
      transition: all 0.15s ease-in-out;
    }

    .nav-link:hover {
      color: #F8FAFC;
      background-color: var(--bg-sidebar-hover);
      text-decoration: none;
    }

    .nav-link.active {
      color: #FFFFFF;
      background-color: #2563EB;
      box-shadow: 0 2px 4px rgba(37, 99, 235, 0.25);
    }

    .nav-icon {
      font-size: 1.1rem;
      line-height: 1;
    }

    .external {
      color: #64748B;
      font-size: 0.8125rem;
    }
    .external:hover {
      color: #CBD5E1;
    }

    .external-icon {
      margin-left: auto;
      font-size: 0.8rem;
      opacity: 0.6;
    }

    .sidebar-footer {
      padding: 16px;
      border-top: 1px solid #1E293B;
      background-color: #0B1120;
    }

    .session-card {
      background-color: #1E293B;
      border-radius: 8px;
      padding: 10px 12px;
      border: 1px solid #334155;
    }

    .session-label {
      font-size: 0.65rem;
      font-weight: 700;
      letter-spacing: 0.05em;
      color: #64748B;
      margin-bottom: 4px;
    }

    .session-role {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 0.8rem;
      font-weight: 700;
    }

    .role-dot {
      width: 8px;
      height: 8px;
      border-radius: 50%;
    }

    .role-admin { color: #FCA5A5; }
    .role-admin .role-dot { background-color: #EF4444; }

    .role-terminal { color: #93C5FD; }
    .role-terminal .role-dot { background-color: #3B82F6; }

    .role-anonymous { color: #CBD5E1; }
    .role-anonymous .role-dot { background-color: #94A3B8; }
  `]
})
export class SidebarComponent {
  readonly session = inject(SessionService);
}
