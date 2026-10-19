import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { CardAuditResponse, TransitCard } from '../../core/models/card.model';
import { BadgeComponent } from '../../shared/components/badge/badge.component';
import { MillimesPipe } from '../../shared/pipes/millimes.pipe';

@Component({
  selector: 'app-audit',
  standalone: true,
  imports: [CommonModule, FormsModule, BadgeComponent, MillimesPipe],
  templateUrl: './audit.component.html',
  styleUrls: ['./audit.component.css']
})
export class AuditComponent implements OnInit, OnDestroy {
  private readonly api = inject(JanusApiService);
  readonly session = inject(SessionService);
  private readonly notification = inject(NotificationService);

  readonly demoCards = signal<TransitCard[]>([]);
  readonly selectedUid = signal('');
  readonly auditData = signal<CardAuditResponse | null>(null);
  readonly isLoading = signal(false);
  readonly rbacError = signal<{ status: number; message: string } | null>(null);

  // Pagination serveur de l'audit (10, 25, 50)
  readonly pageSize = signal<number>(25);
  readonly currentPage = signal<number>(1);
  readonly totalAuditCount = signal<number>(0);
  readonly totalPages = signal<number>(1);

  private sessionSub?: Subscription;
  private cardsSub?: Subscription;
  private auditSub?: Subscription;

  ngOnInit(): void {
    if (this.session.activeRole() === 'ADMIN') {
      this.loadCards();
    }
    this.sessionSub = this.session.sessionChanges$.subscribe((role) => {
      this.onSessionChanged(role);
    });
  }

  ngOnDestroy(): void {
    this.sessionSub?.unsubscribe();
    this.cardsSub?.unsubscribe();
    this.auditSub?.unsubscribe();
  }

  private onSessionChanged(role: string): void {
    this.cardsSub?.unsubscribe();
    this.auditSub?.unsubscribe();

    // Purge immédiate des données d'audit administratives
    this.auditData.set(null);
    this.rbacError.set(null);
    this.demoCards.set([]);
    this.selectedUid.set('');
    this.totalAuditCount.set(0);
    this.totalPages.set(1);
    this.currentPage.set(1);
    this.pageSize.set(25);
    this.isLoading.set(false);

    if (role === 'ADMIN') {
      this.loadCards();
    }
  }

  loadCards(): void {
    if (this.session.activeRole() !== 'ADMIN') {
      return;
    }
    this.cardsSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();

    this.cardsSub = this.api.getDemoCards().subscribe({
      next: (cards) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.demoCards.set(cards);
        if (cards.length > 0 && !this.selectedUid()) {
          this.selectedUid.set(cards[0].uid);
          this.currentPage.set(1);
          this.fetchAuditLogs();
        }
      },
      error: () => {}
    });
  }

  onCardSelected(uid: string): void {
    this.selectedUid.set(uid);
    this.currentPage.set(1);
    this.fetchAuditLogs();
  }

  fetchAuditLogs(): void {
    if (this.session.activeRole() !== 'ADMIN') {
      return;
    }
    const uid = this.selectedUid().trim();
    if (!uid) return;

    this.auditSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();

    this.isLoading.set(true);
    this.rbacError.set(null);
    this.auditData.set(null);

    this.auditSub = this.api.getCardAudit(uid, this.currentPage(), this.pageSize()).subscribe({
      next: (res) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.auditData.set(res);
        this.totalAuditCount.set(res.totalCount);
        this.totalPages.set(res.totalPages || Math.max(1, Math.ceil(res.totalCount / this.pageSize())));
        this.isLoading.set(false);
      },
      error: (err) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.isLoading.set(false);
        this.totalAuditCount.set(0);
        this.totalPages.set(1);
        const status = err.status || 0;
        let message = 'Erreur lors de la récupération du journal d\'audit.';

        if (status === 403) {
          message = 'Accès interdit (403) : Le rôle Opérateur Borne ne peut pas consulter les journaux administratifs. Jeton Administrateur requis.';
        } else if (status === 401) {
          message = 'Non autorisé (401) : Jeton administrateur manquant ou invalide.';
        } else if (status === 404) {
          message = 'Carte inexistante dans le registre.';
        } else if (status === 429) {
          message = 'Quota de requêtes dépassé (429) : Limitation de débit atteinte.';
        }

        this.rbacError.set({ status, message });
      }
    });
  }

  get startIndex(): number {
    return this.totalAuditCount() === 0 ? 0 : (this.currentPage() - 1) * this.pageSize() + 1;
  }

  get endIndex(): number {
    return Math.min(this.currentPage() * this.pageSize(), this.totalAuditCount());
  }

  onPageSizeChange(newSize: number | string): void {
    const parsed = Number(newSize);
    if (parsed === 10 || parsed === 25 || parsed === 50) {
      this.pageSize.set(parsed);
      this.currentPage.set(1);
      this.fetchAuditLogs();
    }
  }

  goToFirstPage(): void {
    if (this.currentPage() > 1) {
      this.currentPage.set(1);
      this.fetchAuditLogs();
    }
  }

  goToPreviousPage(): void {
    if (this.currentPage() > 1) {
      this.currentPage.update(p => p - 1);
      this.fetchAuditLogs();
    }
  }

  goToNextPage(): void {
    if (this.currentPage() < this.totalPages()) {
      this.currentPage.update(p => p + 1);
      this.fetchAuditLogs();
    }
  }

  goToLastPage(): void {
    if (this.currentPage() < this.totalPages()) {
      this.currentPage.set(this.totalPages());
      this.fetchAuditLogs();
    }
  }
}
