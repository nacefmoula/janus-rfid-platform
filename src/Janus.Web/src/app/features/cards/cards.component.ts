import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
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
export class CardsComponent implements OnInit {
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

  ngOnInit(): void {
    this.loadCards();
  }

  loadCards(): void {
    this.isLoading.set(true);
    const useTerminalCompatibleRoute =
      this.session.activeRole() === 'TERMINAL' ||
      (this.session.hasTerminalAuth() && !this.session.hasAdminAuth());
    const request$ = useTerminalCompatibleRoute
      ? this.api.getSimulatorCards()
      : this.api.getDemoCards();

    request$.subscribe({
      next: (data) => {
        this.cards.set(data);
        this.isLoading.set(false);
      },
      error: () => {
        this.isLoading.set(false);
        this.notification.error('Erreur inventaire', 'Impossible de charger la liste des cartes depuis l\'API.');
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
    this.isLoadingDetail.set(true);
    this.api.getCard(card.uid).subscribe({
      next: (detail) => {
        this.selectedCardDetail.set(detail);
        this.isLoadingDetail.set(false);
      },
      error: (err) => {
        this.isLoadingDetail.set(false);
        // Si non autorisé ou 404, on affiche les données de base connues
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
        if (err.status === 401) {
          this.notification.warning('Accès restreint (401)', 'Un jeton de borne ou administrateur est requis pour consulter l\'historique des débits.');
        }
      }
    });
  }

  closeCardDetail(): void {
    this.selectedCardDetail.set(null);
  }

  /**
   * Ouvre la modale d'édition / recharge administrative.
   */
  openEditModal(card: TransitCard): void {
    this.targetCardUid = card.uid;
    this.editLabel = card.label;
    this.editBalanceMillimes = card.balanceMillimes ?? 0;
    this.editStatus = (card.status as CardStatus) || 'Active';
    this.editReason = '';
    this.showEditModal.set(true);
  }

  closeEditModal(): void {
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
    if (!this.targetCardUid) return;

    if (this.editBalanceMillimes < 0) {
      this.notification.warning('Solde invalide', 'Le solde d\'une carte ne peut pas être négatif.');
      return;
    }

    if (!this.editReason.trim()) {
      this.notification.warning('Motif requis', 'Un motif explicite est obligatoire pour alimenter le journal d\'audit.');
      return;
    }

    this.isSubmitting.set(true);
    const body: CardUpsertRequest = {
      label: this.editLabel.trim() || undefined,
      balanceMillimes: this.editBalanceMillimes,
      status: this.editStatus,
      reason: this.editReason.trim()
    };

    this.api.upsertCard(this.targetCardUid, body).subscribe({
      next: (updatedCard) => {
        this.isSubmitting.set(false);
        this.showEditModal.set(false);
        this.notification.success('Carte mise à jour', `La carte ${updatedCard.uid} a été actualisée avec succès.`);
        this.loadCards();

        // Si la fiche détaillée est ouverte sur cette carte, la réactualise
        if (this.selectedCardDetail()?.uid === updatedCard.uid) {
          this.openCardDetail(updatedCard);
        }
      },
      error: (err) => {
        this.isSubmitting.set(false);
        if (err.status === 403) {
          this.notification.error('Opération refusée (403)', 'Un jeton de borne ne peut pas effectuer d\'actes administratifs. Veuillez renseigner le jeton Administrateur.');
        } else if (err.status === 401) {
          this.notification.error('Authentification requise (401)', 'Jeton administrateur manquant ou invalide.');
        } else {
          this.notification.error('Échec de mise à jour', err.error?.message || 'Erreur lors de la mise à jour.');
        }
      }
    });
  }
}
