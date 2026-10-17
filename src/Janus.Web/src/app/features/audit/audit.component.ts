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

  private sessionSub?: Subscription;
  private cardsSub?: Subscription;
  private auditSub?: Subscription;

  ngOnInit(): void {
    this.loadCards();
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
    this.isLoading.set(false);

    if (role !== 'ANONYMOUS') {
      this.loadCards();
    }
  }

  loadCards(): void {
    this.cardsSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();

    this.cardsSub = this.api.getDemoCards().subscribe({
      next: (cards) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.demoCards.set(cards);
        if (cards.length > 0 && !this.selectedUid()) {
          this.selectedUid.set(cards[0].uid);
          this.fetchAuditLogs();
        }
      },
      error: () => {}
    });
  }

  onCardSelected(uid: string): void {
    this.selectedUid.set(uid);
    this.fetchAuditLogs();
  }

  fetchAuditLogs(): void {
    const uid = this.selectedUid().trim();
    if (!uid) return;

    this.auditSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();

    this.isLoading.set(true);
    this.rbacError.set(null);
    this.auditData.set(null);

    this.auditSub = this.api.getCardAudit(uid).subscribe({
      next: (res) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.auditData.set(res);
        this.isLoading.set(false);
      },
      error: (err) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.isLoading.set(false);
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
}
