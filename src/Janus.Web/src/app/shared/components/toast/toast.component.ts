import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { NotificationService } from '../../../core/services/notification.service';

@Component({
  selector: 'app-toast',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="toast-container" aria-live="polite">
      @for (toast of notification.toasts(); track toast.id) {
        <div class="toast" [ngClass]="'toast-' + toast.type">
          <div class="toast-icon">
            @switch (toast.type) {
              @case ('success') { <span>✔</span> }
              @case ('error') { <span>✖</span> }
              @case ('warning') { <span>⚠</span> }
              @default { <span>ℹ</span> }
            }
          </div>
          <div class="toast-content">
            <div class="toast-title">{{ toast.title }}</div>
            <div class="toast-message">{{ toast.message }}</div>
          </div>
          <button class="toast-close" (click)="notification.dismiss(toast.id)" aria-label="Fermer">✕</button>
        </div>
      }
    </div>
  `,
  styles: [`
    .toast-container {
      position: fixed;
      bottom: 24px;
      right: 24px;
      z-index: 9999;
      display: flex;
      flex-direction: column;
      gap: 12px;
      max-width: 420px;
      width: 100%;
      pointer-events: none;
    }
    .toast {
      pointer-events: auto;
      display: flex;
      align-items: flex-start;
      gap: 12px;
      padding: 14px 16px;
      border-radius: 8px;
      background: #1e293b;
      color: #f8fafc;
      box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.5), 0 8px 10px -6px rgba(0, 0, 0, 0.4);
      border-left: 4px solid #94a3b8;
      animation: slideIn 0.25s ease-out;
    }
    @keyframes slideIn {
      from { transform: translateX(100%); opacity: 0; }
      to { transform: translateX(0); opacity: 1; }
    }
    .toast-success { border-left-color: #10b981; }
    .toast-error { border-left-color: #ef4444; }
    .toast-warning { border-left-color: #f59e0b; }
    .toast-info { border-left-color: #3b82f6; }

    .toast-icon {
      font-size: 1.1rem;
      font-weight: bold;
      line-height: 1;
    }
    .toast-success .toast-icon { color: #10b981; }
    .toast-error .toast-icon { color: #ef4444; }
    .toast-warning .toast-icon { color: #f59e0b; }
    .toast-info .toast-icon { color: #3b82f6; }

    .toast-content {
      flex: 1;
    }
    .toast-title {
      font-weight: 600;
      font-size: 0.9rem;
      margin-bottom: 2px;
    }
    .toast-message {
      font-size: 0.8rem;
      color: #cbd5e1;
      word-break: break-word;
    }
    .toast-close {
      background: transparent;
      border: none;
      color: #94a3b8;
      cursor: pointer;
      font-size: 0.9rem;
      padding: 0;
      line-height: 1;
    }
    .toast-close:hover {
      color: #f8fafc;
    }
  `]
})
export class ToastComponent {
  readonly notification = inject(NotificationService);
}
