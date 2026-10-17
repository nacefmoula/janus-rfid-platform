import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { TransitCard, CardDetail, CardUpsertRequest, CardStatus } from '../../core/models/card.model';
import { BadgeComponent } from '../../shared/components/badge/badge.component';
import { MillimesPipe } from '../../shared/pipes/millimes.pipe';

@Component({
  selector: 'app-cards',
  standalone: true,
  imports: [CommonModule, FormsModule, BadgeComponent, MillimesPipe],
  templateUrl: './cards.component.html',
  styleUrls: ['./cards.component.css']
})
export class CardsComponent implements OnInit, OnDestroy {
  private readonly api = inject(JanusApiService);
  readonly session = inject(SessionService);
  private readonly notification = inject(NotificationService);

  readonly cards = signal<TransitCard[]>([]);
  readonly isLoading = signal(false);
  readonly searchFilter = signal('');
  readonly statusFilter = signal<'ALL' | 'Active' | 'Revoked'>('ALL');

  // Fiche détaillée de la carte sélectionnée
  readonly selectedCardDetail = signal<CardDetail | null>(null);
  readonly isLoadingDetail = signal(false);

  // Modale d'administration (Recharge / Statut)
  readonly showEditModal = signal(false);
  readonly isSubmitting = signal(false);

  // Formulaire d'édition
  targetCardUid = '';
  editLabel = '';
  editBalanceMillimes = 0;
  editStatus: CardStatus = 'Active';
  editReason = '';

  private sessionSub?: Subscription;
  private cardsSub?: Subscription;
  private detailSub?: Subscription;
  private updateSub?: Subscription;

  ngOnInit(): void {
    this.loadCards();
    this.sessionSub = this.session.sessionChanges$.subscribe((role) => {
      this.onSessionChanged(role);
    });
  }

  ngOnDestroy(): void {
    this.sessionSub?.unsubscribe();
    this.cardsSub?.unsubscribe();
    this.detailSub?.unsubscribe();
    this.updateSub?.unsubscribe();
  }

  private onSessionChanged(role: string): void {
    this.cardsSub?.unsubscribe();
    this.detailSub?.unsubscribe();
    this.updateSub?.unsubscribe();

    // Purge immédiate des données potentiellement sensibles et fermeture des modales
    this.cards.set([]);
    this.selectedCardDetail.set(null);
    this.isLoadingDetail.set(false);
    this.showEditModal.set(false);
    this.isSubmitting.set(false);
    this.isLoading.set(false);
    this.targetCardUid = '';
    this.editReason = '';

    if (role !== 'ANONYMOUS') {
      this.loadCards();
    }
  }

  loadCards(): void {
    this.cardsSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();
    this.isLoading.set(true);

    const useTerminalCompatibleRoute = this.session.activeRole() === 'TERMINAL';
    const request$ = useTerminalCompatibleRoute
      ? this.api.getSimulatorCards()
      : this.api.getDemoCards();

    this.cardsSub = request$.subscribe({
      next: (data) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.cards.set(data);
        this.isLoading.set(false);
      },
      error: () => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.cards.set([]);
        this.isLoading.set(false);
      }
    });
  }

  get filteredCards(): TransitCard[] {
    const query = this.searchFilter().trim().toUpperCase();
    const status = this.statusFilter();

    return this.cards().filter(card => {
      const matchQuery = !query || card.uid.toUpperCase().includes(query) || card.label.toUpperCase().includes(query);
      const matchStatus = status === 'ALL' || card.status === status;
      return matchQuery && matchStatus;
    });
  }

  /**
   * Ouvre la fiche détaillée d'une carte et consulte l'historique de ses transactions.
   * Requiert un token (Terminal ou Admin).
   */
  openCardDetail(card: TransitCard): void {
    this.detailSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();
    this.isLoadingDetail.set(true);

    this.detailSub = this.api.getCard(card.uid).subscribe({
      next: (detail) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.selectedCardDetail.set(detail);
        this.isLoadingDetail.set(false);
      },
      error: () => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.isLoadingDetail.set(false);
        this.selectedCardDetail.set({
          uid: card.uid,
          status: card.status,
          label: card.label,
          balanceMillimes: card.balanceMillimes ?? 0,
          transactionsCount: 0,
          limit: 50,
          offset: 0,
          transactions: []
        });
      }
    });
  }

  closeCardDetail(): void {
    this.detailSub?.unsubscribe();
    this.isLoadingDetail.set(false);
    this.selectedCardDetail.set(null);
  }

  /**
   * Ouvre la modale d'édition / recharge administrative.
   * Requiert STRICTEMENT le rôle actif Administrateur.
   */
  openEditModal(card: TransitCard): void {
    if (this.session.activeRole() !== 'ADMIN') {
      this.notification.warning('Accès restreint', 'Le rôle Administrateur est requis pour modifier ou recharger une carte.');
      return;
    }
    this.targetCardUid = card.uid;
    this.editLabel = card.label;
    this.editBalanceMillimes = card.balanceMillimes ?? 0;
    this.editStatus = (card.status as CardStatus) || 'Active';
    this.editReason = '';
    this.showEditModal.set(true);
  }

  closeEditModal(): void {
    this.updateSub?.unsubscribe();
    this.isSubmitting.set(false);
    this.showEditModal.set(false);
  }

  /**
   * Ajoute 5, 10 ou 20 TND au solde en millimes dans le formulaire.
   */
  addAmountMillimes(millimesToAdd: number): void {
    this.editBalanceMillimes += millimesToAdd;
  }

  /**
   * Soumet la mise à jour administrative (PUT /api/cards/{uid}).
   * Requiert STRICTEMENT les droits Admin.
   */
  submitCardUpdate(): void {
    if (this.session.activeRole() !== 'ADMIN') {
      this.notification.warning('Accès restreint', 'Le rôle Administrateur est requis pour soumettre des modifications.');
      this.closeEditModal();
      return;
    }
    if (!this.targetCardUid) return;

    if (this.editBalanceMillimes < 0) {
      this.notification.warning('Solde invalide', 'Le solde d\'une carte ne peut pas être négatif.');
      return;
    }

    if (!this.editReason.trim()) {
      this.notification.warning('Motif requis', 'Un motif explicite est obligatoire pour alimenter le journal d\'audit.');
      return;
    }

    this.updateSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();
    this.isSubmitting.set(true);
    const body: CardUpsertRequest = {
      label: this.editLabel.trim() || undefined,
      balanceMillimes: this.editBalanceMillimes,
      status: this.editStatus,
      reason: this.editReason.trim()
    };

    this.updateSub = this.api.upsertCard(this.targetCardUid, body).subscribe({
      next: (updatedCard) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.isSubmitting.set(false);
        this.showEditModal.set(false);
        this.notification.success('Carte mise à jour', `La carte ${updatedCard.uid} a été actualisée avec succès.`);
        this.loadCards();

        // Si la fiche détaillée est ouverte sur cette carte, la réactualise
        if (this.selectedCardDetail()?.uid === updatedCard.uid) {
          this.openCardDetail(updatedCard);
        }
      },
      error: () => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.isSubmitting.set(false);
      }
    });
  }
}
