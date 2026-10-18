import { Injectable, signal, computed } from '@angular/core';

export interface ToastMessage {
  id: string;
  type: 'success' | 'error' | 'warning' | 'info';
  title: string;
  message: string;
  timestamp: Date;
}

@Injectable({
  providedIn: 'root'
})
export class NotificationService {
  private readonly toastsSignal = signal<ToastMessage[]>([]);

  readonly toasts = computed(() => this.toastsSignal());

  show(type: ToastMessage['type'], title: string, message: string, autoDismissMs = 5000): void {
    const id = crypto.randomUUID();
    const toast: ToastMessage = { id, type, title, message, timestamp: new Date() };

    this.toastsSignal.update(list => [...list, toast]);

    if (autoDismissMs > 0) {
      setTimeout(() => this.dismiss(id), autoDismissMs);
    }
  }

  success(title: string, message: string): void {
    this.show('success', title, message);
  }

  error(title: string, message: string): void {
    this.show('error', title, message, 8000);
  }

  warning(title: string, message: string): void {
    this.show('warning', title, message);
  }

  info(title: string, message: string): void {
    this.show('info', title, message);
  }

  dismiss(id: string): void {
    this.toastsSignal.update(list => list.filter(t => t.id !== id));
  }
}
