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
      gap: 5px;
      padding: 2px 7px;
      border-radius: 4px;
      font-size: 0.6875rem;
      font-weight: 600;
      letter-spacing: 0.02em;
      line-height: 1.35;
      white-space: nowrap;
      border: 1px solid transparent;
      text-transform: uppercase;
    }

    .badge-dot {
      width: 5px;
      height: 5px;
      border-radius: 50%;
      flex-shrink: 0;
    }

    /* Succès / Active / Accepté (WCAG AAA) */
    .badge-success {
      background-color: var(--color-success-bg);
      color: var(--color-success-text);
      border-color: var(--color-success-border);
    }
    .badge-success .badge-dot { background-color: var(--color-success); }

    /* Danger / Révoqué / Refusé (WCAG AAA) */
    .badge-danger {
      background-color: var(--color-danger-bg);
      color: var(--color-danger-text);
      border-color: var(--color-danger-border);
    }
    .badge-danger .badge-dot { background-color: var(--color-danger); }

    /* Avertissement / Rejeu / Suspendu (WCAG AA) */
    .badge-warning {
      background-color: var(--color-warning-bg);
      color: var(--color-warning-text);
      border-color: var(--color-warning-border);
    }
    .badge-warning .badge-dot { background-color: var(--color-warning); }

    /* Conflit 409 / Audit (WCAG AA) */
    .badge-purple {
      background-color: var(--color-purple-bg);
      color: var(--color-purple-text);
      border-color: var(--color-purple-border);
    }
    .badge-purple .badge-dot { background-color: var(--color-purple); }

    /* Info / Neutre */
    .badge-neutral {
      background-color: var(--bg-surface-subtle);
      color: var(--color-text-muted);
      border-color: var(--border-light);
    }
    .badge-neutral .badge-dot { background-color: var(--color-text-dim); }
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
