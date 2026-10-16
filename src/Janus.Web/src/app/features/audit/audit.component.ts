import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { CardAuditResponse, CardAuditLog, TransitCard } from '../../core/models/card.model';
import { BadgeComponent } from '../../shared/components/badge/badge.component';
import { MillimesPipe } from '../../shared/pipes/millimes.pipe';

@Component({
  selector: 'app-audit',
  standalone: true,
  imports: [CommonModule, FormsModule, BadgeComponent, MillimesPipe],
  templateUrl: './audit.component.html',
  styleUrls: ['./audit.component.css']
})
export class AuditComponent implements OnInit {
  private readonly api = inject(JanusApiService);
  readonly session = inject(SessionService);
  private readonly notification = inject(NotificationService);

  readonly demoCards = signal<TransitCard[]>([]);
  readonly selectedUid = signal('');
  readonly auditData = signal<CardAuditResponse | null>(null);
  readonly isLoading = signal(false);
  readonly rbacError = signal<{ status: number; message: string } | null>(null);

  ngOnInit(): void {
    this.loadCards();
  }

  loadCards(): void {
    this.api.getDemoCards().subscribe({
      next: (cards) => {
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

    this.isLoading.set(true);
    this.rbacError.set(null);
    this.auditData.set(null);

    this.api.getCardAudit(uid).subscribe({
      next: (res) => {
        this.auditData.set(res);
        this.isLoading.set(false);
      },
      error: (err) => {
        this.isLoading.set(false);
        const status = err.status || 0;
        let message = 'Erreur lors de la récupération du journal d\'audit.';

        if (status === 403) {
          message = 'Accès interdit (403) : Le rôle Opérateur Borne ne peut pas consulter les journaux administratifs. Jeton Administrateur requis.';
        } else if (status === 401) {
          message = 'Non autorisé (401) : Jeton administrateur manquant ou invalide.';
        } else if (status === 404) {
          message = 'Carte inexistante dans le registre.';
        }

        this.rbacError.set({ status, message });
      }
    });
  }
}
