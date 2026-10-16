import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-badge',
  standalone: true,
  imports: [CommonModule],
  template: `
    <span class="badge" [ngClass]="badgeClass">
      <span class="badge-dot" aria-hidden="true"></span>
      <span class="badge-text">{{ displayText }}</span>
    </span>
  `,
  styles: [`
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 10px;
      border-radius: 9999px;
      font-size: 0.75rem;
      font-weight: 600;
      letter-spacing: 0.02em;
      line-height: 1.3;
      white-space: nowrap;
    }

    .badge-dot {
      width: 6px;
      height: 6px;
      border-radius: 50%;
      flex-shrink: 0;
    }

    /* Succès / Active / Accepté (WCAG AAA) */
    .badge-success {
      background-color: #E3FCEF;
      color: #006644;
      border: 1px solid #ABF5D1;
    }
    .badge-success .badge-dot { background-color: #00875A; }

    /* Danger / Révoqué / Refusé (WCAG AAA) */
    .badge-danger {
      background-color: #FFEBE6;
      color: #BF2600;
      border: 1px solid #FFBDAD;
    }
    .badge-danger .badge-dot { background-color: #DE350B; }

    /* Avertissement / Rejeu / Suspendu (WCAG AA) */
    .badge-warning {
      background-color: #FFFAE6;
      color: #8F5500;
      border: 1px solid #FFE380;
    }
    .badge-warning .badge-dot { background-color: #FF8B00; }

    /* Conflit 409 / Audit (WCAG AA) */
    .badge-purple {
      background-color: #EAE6FF;
      color: #403294;
      border: 1px solid #C0B6F2;
    }
    .badge-purple .badge-dot { background-color: #5243AA; }

    /* Info / Neutre */
    .badge-neutral {
      background-color: #F1F5F9;
      color: #334155;
      border: 1px solid #CBD5E1;
    }
    .badge-neutral .badge-dot { background-color: #64748B; }
  `]
})
export class BadgeComponent {
  @Input() value: string | null | undefined = '';

  get displayText(): string {
    const v = this.value || '';
    if (v.toUpperCase() === 'ACTIVE') return 'Active';
    if (v.toUpperCase() === 'REVOKED') return 'Révoquée';
    if (v.toUpperCase() === 'ACCEPTED') return 'Accepté';
    if (v.toUpperCase() === 'REJECTED') return 'Refusé';
    if (v.toUpperCase() === 'CONFLICT') return 'Conflit 409';
    return v || 'Inconnu';
  }

  get badgeClass(): string {
    const v = (this.value || '').toUpperCase();
    if (v === 'ACTIVE' || v === 'ACCEPTED' || v === 'HEALTHY' || v === 'OK') {
      return 'badge-success';
    }
    if (v === 'REVOKED' || v === 'REJECTED' || v === 'UNHEALTHY' || v === 'ERROR') {
      return 'badge-danger';
    }
    if (v === 'SUSPENDED' || v === 'DUPLICATE' || v === 'WARNING') {
      return 'badge-warning';
    }
    if (v === 'CONFLICT' || v === 'IDEMPOTENCY_CONFLICT') {
      return 'badge-purple';
    }
    return 'badge-neutral';
  }
}
