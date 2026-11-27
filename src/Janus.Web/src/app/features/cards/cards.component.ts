import { Component, inject, OnInit, OnDestroy, signal, HostListener } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Observable, Subscription, Subject, debounceTime, distinctUntilChanged, of, switchMap } from 'rxjs';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { TransitCard, CardDetail, CardUpsertRequest, CardStatus } from '../../core/models/card.model';
import { newOperationId } from '../../core/utils/operation-id';
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
  readonly totalCardsCount = signal<number>(0);
  readonly totalPagesCount = signal<number>(1);
  readonly isLoading = signal(false);
  readonly searchFilter = signal('');
  readonly statusFilter = signal<'ALL' | 'Active' | 'Revoked'>('ALL');

  // Pagination (tailles autorisées : 10, 25, 50)
  readonly pageSize = signal<number>(10);
  readonly currentPage = signal<number>(1);

  // Fiche détaillée de la carte sélectionnée
  readonly selectedCardDetail = signal<CardDetail | null>(null);
  readonly isLoadingDetail = signal(false);

  // Modale d'administration (Recharge / Statut)
  readonly showEditModal = signal(false);
  readonly isSubmitting = signal(false);
  readonly modalErrorMessage = signal<string | null>(null);
  readonly selectedCardForEdit = signal<TransitCard | null>(null);

  // Focus management pour l'accessibilité
  private lastFocusedElement: HTMLElement | null = null;

  // Formulaire d'édition
  targetCardUid = '';
  editLabel = '';
  /** Solde lu à l'ouverture de la modale (lecture seule : le solde n'est jamais saisi en valeur absolue). */
  editCurrentBalanceMillimes = 0;
  /** Montant à créditer (delta, en millimes) via POST /api/cards/{uid}/credits. */
  editCreditMillimes = 0;
  editStatus: CardStatus = 'Active';
  private originalLabel = '';
  private originalStatus: CardStatus = 'Active';
  /** Identifiant d'idempotence du crédit : conservé tant que le montant ne change pas (nouvel essai sûr). */
  private creditOperation: { id: string; amount: number } | null = null;
  editReason = '';

  private readonly searchSubject = new Subject<string>();
  private searchSub?: Subscription;
  private sessionSub?: Subscription;
  private cardsSub?: Subscription;
  private detailSub?: Subscription;
  private updateSub?: Subscription;

  ngOnInit(): void {
    this.searchSub = this.searchSubject.pipe(
      debounceTime(300),
      distinctUntilChanged()
    ).subscribe((term) => {
      this.searchFilter.set(term);
      this.currentPage.set(1);
      this.loadCards();
    });

    this.loadCards();
    this.sessionSub = this.session.sessionChanges$.subscribe((role) => {
      this.onSessionChanged(role);
    });
  }

  ngOnDestroy(): void {
    this.searchSub?.unsubscribe();
    this.sessionSub?.unsubscribe();
    this.cardsSub?.unsubscribe();
    this.detailSub?.unsubscribe();
    this.updateSub?.unsubscribe();
  }

  private onSessionChanged(role: string): void {
    this.cardsSub?.unsubscribe();
    this.detailSub?.unsubscribe();
    this.updateSub?.unsubscribe();

    // Purge immédiate des données et fermeture des modales
    this.cards.set([]);
    this.totalCardsCount.set(0);
    this.totalPagesCount.set(1);
    this.selectedCardDetail.set(null);
    this.selectedCardForEdit.set(null);
    this.isLoadingDetail.set(false);
    this.showEditModal.set(false);
    this.isSubmitting.set(false);
    this.modalErrorMessage.set(null);
    this.isLoading.set(false);
    this.targetCardUid = '';
    this.editReason = '';
    this.lastFocusedElement = null;
    this.currentPage.set(1);

    if (role !== 'ANONYMOUS') {
      this.loadCards();
    }
  }

  loadCards(): void {
    this.cardsSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();
    this.isLoading.set(true);

    const isTerminal = this.session.activeRole() === 'TERMINAL';

    if (isTerminal) {
      // Pour les terminaux opérationnels, interrogation de la route simulateur minimale
      this.cardsSub = this.api.getSimulatorCards().subscribe({
        next: (data) => {
          if (this.session.sessionVersion() !== expectedVersion) return;
          const query = this.searchFilter().trim().toUpperCase();
          const status = this.statusFilter();
          const filtered = data.filter(c => {
            const matchQ = !query || c.uid.toUpperCase().includes(query) || (c.label && c.label.toUpperCase().includes(query));
            const matchS = status === 'ALL' || c.status === status;
            return matchQ && matchS;
          });
          const total = filtered.length;
          const pages = Math.max(1, Math.ceil(total / this.pageSize()));
          if (this.currentPage() > pages) {
            this.currentPage.set(pages);
          }
          const start = (this.currentPage() - 1) * this.pageSize();
          this.cards.set(filtered.slice(start, start + this.pageSize()));
          this.totalCardsCount.set(total);
          this.totalPagesCount.set(pages);
          this.isLoading.set(false);
        },
        error: () => {
          if (this.session.sessionVersion() !== expectedVersion) return;
          this.cards.set([]);
          this.totalCardsCount.set(0);
          this.totalPagesCount.set(1);
          this.isLoading.set(false);
        }
      });
    } else {
      // Pour les administrateurs, pagination et filtrage 100% côté serveur via GET /api/cards
      const q = this.searchFilter().trim() || undefined;
      const status = this.statusFilter() !== 'ALL' ? this.statusFilter() : undefined;

      this.cardsSub = this.api.getCards(this.currentPage(), this.pageSize(), q, status).subscribe({
        next: (res) => {
          if (this.session.sessionVersion() !== expectedVersion) return;
          this.cards.set(res.items);
          this.totalCardsCount.set(res.totalCount);
          this.totalPagesCount.set(res.totalPages);
          this.isLoading.set(false);

          // Si la page courante dépasse le nombre total de pages disponible (ex: suppression ou filtre restrictif)
          if (res.totalCount > 0 && this.currentPage() > res.totalPages) {
            this.currentPage.set(res.totalPages);
            this.loadCards();
          }
        },
        error: () => {
          if (this.session.sessionVersion() !== expectedVersion) return;
          this.cards.set([]);
          this.totalCardsCount.set(0);
          this.totalPagesCount.set(1);
          this.isLoading.set(false);
        }
      });
    }
  }

  get filteredCards(): TransitCard[] {
    return this.cards();
  }

  get paginatedCards(): TransitCard[] {
    return this.cards();
  }

  get totalFilteredCards(): number {
    return this.totalCardsCount();
  }

  get totalPages(): number {
    return Math.max(1, this.totalPagesCount());
  }

  get startIndex(): number {
    return this.totalFilteredCards === 0 ? 0 : (this.currentPage() - 1) * this.pageSize() + 1;
  }

  get endIndex(): number {
    return Math.min(this.currentPage() * this.pageSize(), this.totalFilteredCards);
  }

  get hasActiveFilters(): boolean {
    return !!this.searchFilter().trim() || this.statusFilter() !== 'ALL';
  }

  onSearchInput(value: string): void {
    this.currentPage.set(1);
    this.searchSubject.next(value);
  }

  onStatusFilterChange(value: 'ALL' | 'Active' | 'Revoked'): void {
    this.statusFilter.set(value);
    this.currentPage.set(1);
    this.loadCards();
  }

  onPageSizeChange(newSize: number | string): void {
    const parsed = Number(newSize);
    if (parsed === 10 || parsed === 25 || parsed === 50) {
      this.pageSize.set(parsed);
      this.currentPage.set(1);
      this.loadCards();
    }
  }

  resetFilters(): void {
    this.searchFilter.set('');
    this.statusFilter.set('ALL');
    this.currentPage.set(1);
    this.loadCards();
  }

  goToFirstPage(): void {
    if (this.currentPage() > 1) {
      this.currentPage.set(1);
      this.loadCards();
    }
  }

  goToPreviousPage(): void {
    if (this.currentPage() > 1) {
      this.currentPage.update(p => p - 1);
      this.loadCards();
    }
  }

  goToNextPage(): void {
    if (this.currentPage() < this.totalPages) {
      this.currentPage.update(p => p + 1);
      this.loadCards();
    }
  }

  goToLastPage(): void {
    if (this.currentPage() < this.totalPages) {
      this.currentPage.set(this.totalPages);
      this.loadCards();
    }
  }

  private clampCurrentPage(): void {
    const max = this.totalPages;
    if (this.currentPage() > max) {
      this.currentPage.set(max);
    }
  }

  // --- Gestion du focus & Raccourcis clavier accessibles ---
  // Angular 21 type $event comme Event pour les écouteurs document:* ; seul preventDefault() est utilisé.
  @HostListener('document:keydown.escape', ['$event'])
  onEscapeKey(event: Event): void {
    if (this.showEditModal()) {
      if (!this.isSubmitting()) {
        event.preventDefault();
        this.closeEditModal();
      }
    } else if (this.selectedCardDetail()) {
      event.preventDefault();
      this.closeCardDetail();
    }
  }

  @HostListener('keydown', ['$event'])
  onTabTrap(event: KeyboardEvent): void {
    if (event.key !== 'Tab' || !this.showEditModal()) return;

    const modalDialog = document.querySelector('.modal-dialog[role="dialog"]');
    if (!modalDialog) return;

    const focusables = Array.from(
      modalDialog.querySelectorAll<HTMLElement>(
        'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])'
      )
    ).filter(el => el.offsetParent !== null);

    if (focusables.length === 0) return;

    const first = focusables[0];
    const last = focusables[focusables.length - 1];

    if (event.shiftKey) {
      if (document.activeElement === first) {
        event.preventDefault();
        last.focus();
      }
    } else {
      if (document.activeElement === last) {
        event.preventDefault();
        first.focus();
      }
    }
  }

  onBackdropClick(event: MouseEvent): void {
    if (event.target === event.currentTarget && !this.isSubmitting()) {
      this.closeEditModal();
    }
  }

  // --- Fiche détaillée ---
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

  // --- Modale d'administration (Recharge / Statut) ---
  openEditModal(card: TransitCard, triggerElement?: HTMLElement): void {
    if (this.session.activeRole() !== 'ADMIN') {
      this.notification.warning('Accès restreint', 'Le rôle Administrateur est requis pour modifier ou recharger une carte.');
      return;
    }

    this.lastFocusedElement = triggerElement || (document.activeElement as HTMLElement) || null;
    this.targetCardUid = card.uid;
    this.selectedCardForEdit.set(card);
    this.editLabel = card.label || '';
    this.originalLabel = this.editLabel;
    this.editCurrentBalanceMillimes = card.balanceMillimes ?? 0;
    this.editCreditMillimes = 0;
    this.editStatus = (card.status as CardStatus) || 'Active';
    this.originalStatus = this.editStatus;
    this.editReason = '';
    this.creditOperation = null;
    this.modalErrorMessage.set(null);
    this.showEditModal.set(true);

    setTimeout(() => {
      const modalDialog = document.querySelector('.modal-dialog[role="dialog"]');
      const firstInput = modalDialog?.querySelector<HTMLElement>('#cardLabelInput, input, button');
      firstInput?.focus();
    }, 50);
  }

  closeEditModal(): void {
    if (this.isSubmitting()) return;
    this.updateSub?.unsubscribe();
    this.isSubmitting.set(false);
    this.modalErrorMessage.set(null);
    this.showEditModal.set(false);
    this.selectedCardForEdit.set(null);
    this.creditOperation = null;

    if (this.lastFocusedElement) {
      try {
        this.lastFocusedElement.focus();
      } catch {}
      this.lastFocusedElement = null;
    }
  }

  /** Raccourci de recharge rapide : augmente le montant à créditer (jamais le solde directement). */
  addAmountMillimes(millimesToAdd: number): void {
    this.editCreditMillimes += millimesToAdd;
  }

  /** Solde estimé après crédit, affiché à titre indicatif (le serveur fait foi). */
  get projectedBalanceMillimes(): number {
    return this.editCurrentBalanceMillimes + (this.editCreditMillimes > 0 ? this.editCreditMillimes : 0);
  }

  private get hasMetadataChange(): boolean {
    return this.editLabel.trim() !== this.originalLabel.trim() || this.editStatus !== this.originalStatus;
  }

  /** Réutilise l'operationId tant que le montant est identique : un nouvel essai après erreur réseau ne crédite qu'une fois. */
  private creditOperationIdFor(amount: number): string {
    if (!this.creditOperation || this.creditOperation.amount !== amount) {
      this.creditOperation = { id: newOperationId(), amount };
    }
    return this.creditOperation.id;
  }

  private rejectSubmission(title: string, message: string): void {
    this.modalErrorMessage.set(message);
    this.notification.warning(title, message);
  }

  submitCardUpdate(): void {
    if (this.session.activeRole() !== 'ADMIN') {
      this.notification.warning('Accès restreint', 'Le rôle Administrateur est requis pour soumettre des modifications.');
      this.closeEditModal();
      return;
    }
    if (!this.targetCardUid) return;

    const credit = Number(this.editCreditMillimes);
    if (!Number.isInteger(credit) || credit < 0) {
      this.rejectSubmission('Montant invalide', 'Le montant à créditer doit être un nombre entier de millimes, jamais négatif.');
      return;
    }

    const reason = this.editReason.trim();
    if (!reason) {
      this.rejectSubmission('Motif requis', 'Un motif explicite est obligatoire pour alimenter le journal d\'audit.');
      return;
    }

    const metadataChange = this.hasMetadataChange;
    if (!metadataChange && credit === 0) {
      this.rejectSubmission('Aucune modification', 'Aucune modification à enregistrer : saisissez un montant à créditer ou modifiez le libellé / statut.');
      return;
    }

    // Règle métier serveur : aucune valeur n'est créditée sur une carte en opposition.
    if (credit > 0 && this.editStatus === 'Revoked') {
      this.rejectSubmission('Crédit impossible', 'Une carte révoquée ne peut pas être créditée. Laissez-la active pour la recharger.');
      return;
    }

    this.modalErrorMessage.set(null);
    this.updateSub?.unsubscribe();
    const expectedVersion = this.session.sessionVersion();
    const uid = this.targetCardUid;
    this.isSubmitting.set(true);

    // 1. Libellé / statut : PUT sans solde (le solde n'est jamais réécrit par l'interface).
    // 2. Recharge : crédit delta idempotent. Ordre choisi pour permettre "réactiver puis créditer".
    const metadataBody: CardUpsertRequest = {
      label: this.editLabel.trim() || undefined,
      status: this.editStatus,
      reason
    };
    let metadataSaved = false;
    const metadata$: Observable<unknown> = metadataChange ? this.api.upsertCard(uid, metadataBody) : of(null);
    const operationId = credit > 0 ? this.creditOperationIdFor(credit) : null;

    this.updateSub = metadata$.pipe(
      switchMap(() => {
        metadataSaved = metadataChange;
        if (metadataChange) {
          this.originalLabel = this.editLabel.trim();
          this.originalStatus = this.editStatus;
        }
        return operationId ? this.api.creditCard(uid, { operationId, amountMillimes: credit, reason }) : of(null);
      })
    ).subscribe({
      next: (creditResult) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.isSubmitting.set(false);
        this.showEditModal.set(false);
        this.selectedCardForEdit.set(null);
        this.creditOperation = null;
        const detail = creditResult
          ? `Crédit de ${(creditResult.amountMillimes / 1000).toFixed(3)} TND enregistré${creditResult.duplicate ? ' (déjà appliqué, aucun double crédit)' : ''}.`
          : 'Libellé / statut mis à jour.';
        this.notification.success('Carte mise à jour', `Carte ${uid} : ${detail}`);
        this.loadCards();

        if (this.lastFocusedElement) {
          try {
            this.lastFocusedElement.focus();
          } catch {}
          this.lastFocusedElement = null;
        }

        if (this.selectedCardDetail()?.uid === uid) {
          this.openCardDetail({ uid, status: this.editStatus, label: this.editLabel.trim(), balanceMillimes: creditResult?.balanceAfterMillimes });
        }
      },
      error: (err: { status?: number; error?: { error?: string; message?: string } }) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.isSubmitting.set(false);
        const code = err?.error?.error;
        const prefix = metadataSaved ? 'Libellé / statut enregistrés, mais le crédit a échoué : ' : '';

        if (err?.status === 409 && code === 'card_revoked') {
          this.modalErrorMessage.set(prefix + 'la carte est révoquée et ne peut pas être créditée.');
          return;
        }
        if (err?.status === 409 && code === 'idempotency_conflict') {
          // L'identifiant a déjà servi pour un autre crédit : on en génère un nouveau pour le prochain essai.
          this.creditOperation = null;
          this.modalErrorMessage.set(prefix + 'conflit d\'identifiant d\'opération. Vérifiez l\'historique de la carte avant de réessayer.');
          return;
        }
        if (err?.status === 400 && (code === 'invalid_amount' || code === 'invalid_reason')) {
          this.modalErrorMessage.set(prefix + (err.error?.message ?? 'Données de crédit invalides.'));
          return;
        }
        if (metadataSaved) {
          this.modalErrorMessage.set(prefix + 'réessayez : le même identifiant d\'opération sera réutilisé, sans risque de double crédit.');
          return;
        }
        this.modalErrorMessage.set('Échec de la mise à jour de la carte. Vérifiez vos droits administrateur ou la validité des données.');
      }
    });
  }
}
