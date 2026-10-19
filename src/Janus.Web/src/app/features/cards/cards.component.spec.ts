import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { CardsComponent } from './cards.component';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { CardDetail, TransitCard, PagedResult } from '../../core/models/card.model';

describe('CardsComponent', () => {
  let component: CardsComponent;
  let fixture: ComponentFixture<CardsComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;
  let notification: NotificationService;

  const mockCards: TransitCard[] = [
    { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte Usager Régulier', balanceMillimes: 10000 },
    { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte Opposition', balanceMillimes: 0 }
  ];

  const mockSimulatorCards = [
    { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte Usager Régulier' },
    { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte Opposition' }
  ];

  const fakeGetCards = (page = 1, pageSize = 10, q?: string, status?: string) => {
    let filtered = mockCards;
    if (q) {
      const upper = q.toUpperCase();
      filtered = filtered.filter(c => c.uid.toUpperCase().includes(upper) || c.label.toUpperCase().includes(upper));
    }
    if (status && status !== 'ALL') {
      filtered = filtered.filter(c => c.status === status);
    }
    const total = filtered.length;
    const pages = Math.max(1, Math.ceil(total / pageSize));
    const start = (page - 1) * pageSize;
    const items = filtered.slice(start, start + pageSize);
    return of({
      items,
      page,
      pageSize,
      totalCount: total,
      totalPages: pages
    });
  };

  beforeEach(async () => {
    apiSpy = jasmine.createSpyObj('JanusApiService', ['getCards', 'getDemoCards', 'getSimulatorCards', 'getCard', 'upsertCard']);
    apiSpy.getCards.and.callFake(fakeGetCards);
    apiSpy.getDemoCards.and.returnValue(of(mockCards));
    apiSpy.getSimulatorCards.and.returnValue(of(mockSimulatorCards));
    apiSpy.getCard.and.returnValue(of({
      uid: '04A1B2C3D4E5F6',
      status: 'Active',
      label: 'Carte Usager Régulier',
      balanceMillimes: 10000,
      transactionsCount: 0,
      limit: 50,
      offset: 0,
      transactions: []
    }));

    await TestBed.configureTestingModule({
      imports: [CardsComponent],
      providers: [
        { provide: JanusApiService, useValue: apiSpy },
        SessionService,
        NotificationService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CardsComponent);
    component = fixture.componentInstance;
    notification = TestBed.inject(NotificationService);
    fixture.detectChanges();
  });

  it('devrait créer le composant et lister les cartes', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getCards).toHaveBeenCalled();
    expect(component.cards().length).toBe(2);
  });

  it('7. devrait rafraîchir automatiquement via getSimulatorCards lorsque la session passe à TERMINAL sans appeler getCards', () => {
    apiSpy.getCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.session.setTerminalToken('valid-terminal-session-token');

    expect(apiSpy.getSimulatorCards).toHaveBeenCalledTimes(1);
    expect(apiSpy.getCards).not.toHaveBeenCalled();
    expect(component.cards().length).toBe(2);
    expect(component.cards()[0].balanceMillimes).toBeUndefined();
  });

  it('7. devrait rafraîchir automatiquement via getCards lorsque la session passe à ADMIN', () => {
    apiSpy.getCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.session.setAdminToken('valid-admin-session-token');

    expect(apiSpy.getCards).toHaveBeenCalledTimes(1);
    expect(apiSpy.getSimulatorCards).not.toHaveBeenCalled();
    expect(component.cards()[0].balanceMillimes).toBe(10000);
  });

  it('ne devrait pas déclencher la route terminale getSimulatorCards en rôle ANONYMOUS même si un jeton terminal réside encore en mémoire', () => {
    component.session.configureSession({
      terminalToken: 'residual-terminal-token',
      adminToken: null,
      role: 'ANONYMOUS'
    });
    apiSpy.getCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.loadCards();

    expect(apiSpy.getSimulatorCards).not.toHaveBeenCalled();
    expect(apiSpy.getCards).toHaveBeenCalledTimes(1);
  });

  it('devrait filtrer les cartes par UID ou libellé de recherche', () => {
    component.searchFilter.set('DEAD');
    component.loadCards();
    expect(component.filteredCards.length).toBe(1);
    expect(component.filteredCards[0].uid).toBe('04DEAD00000001');

    component.searchFilter.set('Régulier');
    component.loadCards();
    expect(component.filteredCards.length).toBe(1);
    expect(component.filteredCards[0].uid).toBe('04A1B2C3D4E5F6');
  });

  it('devrait filtrer les cartes par statut d\'exploitation', () => {
    component.statusFilter.set('Active');
    component.loadCards();
    expect(component.filteredCards.length).toBe(1);
    expect(component.filteredCards[0].status).toBe('Active');

    component.statusFilter.set('Revoked');
    component.loadCards();
    expect(component.filteredCards.length).toBe(1);
    expect(component.filteredCards[0].status).toBe('Revoked');
  });

  it('devrait ouvrir la fiche détaillée d\'une carte et charger l\'historique', () => {
    const mockDetail: CardDetail = {
      uid: '04A1B2C3D4E5F6',
      status: 'Active',
      label: 'Carte Usager Régulier',
      balanceMillimes: 10000,
      transactionsCount: 1,
      limit: 50,
      offset: 0,
      transactions: [
        {
          id: 1,
          terminalId: 'TERMINAL-DEMO-01',
          eventId: 'uuid-1',
          amountMillimes: 1500,
          balanceAfterMillimes: 8500,
          createdAt: '2026-10-16T10:00:00Z'
        }
      ]
    };
    apiSpy.getCard.and.returnValue(of(mockDetail));

    component.openCardDetail(mockCards[0]);

    expect(apiSpy.getCard).toHaveBeenCalledWith('04A1B2C3D4E5F6');
    expect(component.selectedCardDetail()).toEqual(mockDetail);
  });

  it('désactive les boutons d\'administration des cartes et affiche l\'infobulle requise pour un rôle non Admin', () => {
    fixture.detectChanges();

    const manageBtn: HTMLButtonElement | null = fixture.nativeElement.querySelector('.row-actions .btn-disabled-role');
    expect(manageBtn).toBeTruthy();
    expect(manageBtn?.disabled).toBeTrue();
    expect(manageBtn?.title).toContain('Rôle Administrateur requis');
    expect(manageBtn?.textContent).toContain('Gérer (Admin requis)');

    // Fiche détaillée
    component.openCardDetail(mockCards[0]);
    fixture.detectChanges();

    const modalBtn: HTMLButtonElement | null = fixture.nativeElement.querySelector('.modal-footer .btn-disabled-role');
    expect(modalBtn).toBeTruthy();
    expect(modalBtn?.disabled).toBeTrue();
    expect(modalBtn?.title).toContain('Rôle Administrateur requis');
    expect(modalBtn?.textContent).toContain('Modifier / Recharger (Admin requis)');
  });

  it('rejette l\'appel programmatique à openEditModal par un non-Admin et affiche une notification d\'avertissement', () => {
    expect(component.session.activeRole()).not.toBe('ADMIN');

    component.openEditModal(mockCards[0]);

    expect(component.showEditModal()).toBeFalse();
    const toasts = notification.toasts();
    expect(toasts.length).toBe(1);
    expect(toasts[0].type).toBe('warning');
    expect(toasts[0].message.toLowerCase()).toContain('administrateur');
  });

  it('rejette l\'appel programmatique à submitCardUpdate par un non-Admin sans déclencher d\'appel API', () => {
    expect(component.session.activeRole()).not.toBe('ADMIN');
    component.targetCardUid = mockCards[0].uid;

    component.submitCardUpdate();

    expect(apiSpy.upsertCard).not.toHaveBeenCalled();
    const toasts = notification.toasts();
    expect(toasts.some(t => t.type === 'warning' && t.message.toLowerCase().includes('administrateur'))).toBeTrue();
  });

  it('active l\'accès complet aux boutons et aux fonctions de modification en mode ADMIN', () => {
    component.session.setAdminToken('valid-admin-token');
    fixture.detectChanges();

    const manageBtn: HTMLButtonElement | null = fixture.nativeElement.querySelector('.row-actions .btn-primary');
    expect(manageBtn).toBeTruthy();
    expect(manageBtn?.disabled).toBeFalse();
    expect(manageBtn?.textContent).toContain('Gérer / Recharger');

    component.openEditModal(mockCards[0]);
    expect(component.showEditModal()).toBeTrue();
    expect(component.targetCardUid).toBe('04A1B2C3D4E5F6');
  });

  it('devrait soumettre la mise à jour d\'une carte avec motif d\'audit obligatoire en mode ADMIN', () => {
    component.session.setAdminToken('valid-admin-token');
    apiSpy.upsertCard.and.returnValue(of({
      uid: '04A1B2C3D4E5F6',
      status: 'Active',
      label: 'Carte Usager Régulier',
      balanceMillimes: 15000
    }));

    component.openEditModal(mockCards[0]);
    component.addAmountMillimes(5000); // 10000 + 5000 = 15000
    component.editReason = 'Recharge guichet centrale 5 TND';

    component.submitCardUpdate();

    expect(apiSpy.upsertCard).toHaveBeenCalledWith('04A1B2C3D4E5F6', {
      label: 'Carte Usager Régulier',
      balanceMillimes: 15000,
      status: 'Active',
      reason: 'Recharge guichet centrale 5 TND'
    });
    expect(component.showEditModal()).toBeFalse();
  });

  it('6. clearTokens() vide les cartes chargées et ferme les modales de détail et d\'édition', () => {
    component.session.setAdminToken('valid-admin-token');
    const mockDetail: CardDetail = {
      uid: '04A1B2C3D4E5F6',
      status: 'Active',
      label: 'Carte Usager Régulier',
      balanceMillimes: 10000,
      transactionsCount: 0,
      limit: 50,
      offset: 0,
      transactions: []
    };
    apiSpy.getCard.and.returnValue(of(mockDetail));

    component.openCardDetail(mockCards[0]);
    component.openEditModal(mockCards[0]);
    expect(component.selectedCardDetail()).not.toBeNull();
    expect(component.showEditModal()).toBeTrue();
    expect(component.cards().length).toBe(2);

    component.session.clearTokens();

    expect(component.cards()).toEqual([]);
    expect(component.selectedCardDetail()).toBeNull();
    expect(component.showEditModal()).toBeFalse();
    expect(component.targetCardUid).toBe('');
  });

  it('8. une réponse tardive issue d\'une ancienne session n\'écrase pas le nouvel état', () => {
    const delayedAdminCards$ = new Subject<PagedResult<TransitCard>>();
    apiSpy.getCards.and.returnValue(delayedAdminCards$.asObservable());

    // Déclenche une requête en cours sous l'ancienne session
    component.loadCards();
    expect(component.isLoading()).toBeTrue();

    // Changement de session vers ANONYMOUS avant la réponse
    component.session.clearTokens();
    expect(component.cards()).toEqual([]);
    expect(component.isLoading()).toBeFalse();

    // L'ancienne réponse arrive enfin : elle doit être ignorée
    delayedAdminCards$.next({
      items: mockCards,
      page: 1,
      pageSize: 10,
      totalCount: 2,
      totalPages: 1
    });
    expect(component.cards()).toEqual([]);
  });

  it('10. n\'émet pas de toast d\'erreur en doublon depuis le composant lors d\'un échec upsertCard', () => {
    component.session.setAdminToken('valid-admin-token');
    apiSpy.upsertCard.and.returnValue(throwError(() => ({
      status: 403,
      error: { error: 'Accès refusé' }
    })));

    component.openEditModal(mockCards[0]);
    component.editReason = 'Test erreur 403';
    component.submitCardUpdate();

    expect(component.isSubmitting()).toBeFalse();
    // Le toast global est géré par errorInterceptor, le composant ne doit pas en émettre un second
    expect(notification.toasts().length).toBe(0);
  });

  // --- NOUVEAUX TESTS UI-2.2 : MODALE ACCESSIBLE & PAGINATION ---

  describe('Modale d\'administration accessible', () => {
    beforeEach(() => {
      component.session.setAdminToken('valid-admin-token');
      fixture.detectChanges();
    });

    it('devrait ouvrir la modale d\'édition avec les informations complètes de la bonne carte', () => {
      component.openEditModal(mockCards[0]);
      fixture.detectChanges();

      expect(component.showEditModal()).toBeTrue();
      expect(component.targetCardUid).toBe('04A1B2C3D4E5F6');
      expect(component.selectedCardForEdit()).toEqual(mockCards[0]);
      expect(component.editLabel).toBe('Carte Usager Régulier');
      expect(component.editBalanceMillimes).toBe(10000);
      expect(component.editStatus).toBe('Active');

      const dialog: HTMLElement | null = fixture.nativeElement.querySelector('.modal-dialog[role="dialog"]');
      expect(dialog).toBeTruthy();
      expect(dialog?.getAttribute('aria-modal')).toBe('true');
      expect(dialog?.getAttribute('aria-labelledby')).toBe('editModalTitle');

      const cardSummary: HTMLElement | null = dialog?.querySelector('.card-summary-card') || null;
      expect(cardSummary?.textContent).toContain('04A1B2C3D4E5F6');
      expect(cardSummary?.textContent).toContain('10,000 TND');
    });

    it('devrait mémoriser l\'élément déclencheur et lui restituer le focus à la fermeture', () => {
      const triggerBtn = document.createElement('button');
      document.body.appendChild(triggerBtn);
      triggerBtn.focus();
      expect(document.activeElement).toBe(triggerBtn);

      component.openEditModal(mockCards[0], triggerBtn);
      fixture.detectChanges();
      expect(component.showEditModal()).toBeTrue();

      component.closeEditModal();
      fixture.detectChanges();
      expect(component.showEditModal()).toBeFalse();
      expect(document.activeElement).toBe(triggerBtn);

      document.body.removeChild(triggerBtn);
    });

    it('ferme la modale sur la touche Escape uniquement lorsqu\'aucune soumission n\'est en cours', () => {
      component.openEditModal(mockCards[0]);
      fixture.detectChanges();
      expect(component.showEditModal()).toBeTrue();

      // Soumission en cours : Escape doit être ignoré
      component.isSubmitting.set(true);
      const escapeEvent = new KeyboardEvent('keydown', { key: 'Escape' });
      component.onEscapeKey(escapeEvent);
      expect(component.showEditModal()).toBeTrue();

      // Soumission terminée : Escape ferme la modale
      component.isSubmitting.set(false);
      component.onEscapeKey(escapeEvent);
      expect(component.showEditModal()).toBeFalse();
    });

    it('ferme la modale au clic sur le backdrop uniquement lorsqu\'aucune soumission n\'est en cours', () => {
      component.openEditModal(mockCards[0]);
      fixture.detectChanges();

      const backdrop: HTMLElement = fixture.nativeElement.querySelector('.modal-backdrop');
      expect(backdrop).toBeTruthy();

      // En cours de soumission : clic ignoré
      component.isSubmitting.set(true);
      component.onBackdropClick({ target: backdrop, currentTarget: backdrop } as any);
      expect(component.showEditModal()).toBeTrue();

      // Soumission terminée : clic ferme la modale
      component.isSubmitting.set(false);
      component.onBackdropClick({ target: backdrop, currentTarget: backdrop } as any);
      expect(component.showEditModal()).toBeFalse();
    });

    it('refuse la soumission avec un solde négatif ou un motif vide et consigne l\'erreur dans la modale', () => {
      component.openEditModal(mockCards[0]);

      // Solde négatif
      component.editBalanceMillimes = -100;
      component.editReason = 'Motif valide';
      component.submitCardUpdate();
      expect(apiSpy.upsertCard).not.toHaveBeenCalled();
      expect(component.modalErrorMessage()).toContain('négatif');

      // Motif vide
      component.editBalanceMillimes = 5000;
      component.editReason = '   ';
      component.submitCardUpdate();
      expect(apiSpy.upsertCard).not.toHaveBeenCalled();
      expect(component.modalErrorMessage()).toContain('motif explicite est obligatoire');
    });

    it('affiche le message d\'erreur dans la modale et réactive les boutons en cas d\'échec API lors de la mise à jour', () => {
      apiSpy.upsertCard.and.returnValue(throwError(() => new Error('Erreur réseau')));

      component.openEditModal(mockCards[0]);
      component.editReason = 'Recharge 5 TND';
      component.submitCardUpdate();

      expect(component.isSubmitting()).toBeFalse();
      expect(component.showEditModal()).toBeTrue();
      expect(component.modalErrorMessage()).toContain('Échec de la mise à jour');
    });

    it('ferme la modale, rafraîchit les cartes et notifie le succès lors d\'une mise à jour réussie', () => {
      apiSpy.upsertCard.and.returnValue(of({
        uid: '04A1B2C3D4E5F6',
        status: 'Active',
        label: 'Nouveau Libellé',
        balanceMillimes: 20000
      }));
      spyOn(component, 'loadCards').and.callThrough();

      component.openEditModal(mockCards[0]);
      component.editLabel = 'Nouveau Libellé';
      component.editBalanceMillimes = 20000;
      component.editReason = 'Recharge 10 TND';
      component.submitCardUpdate();

      expect(component.isSubmitting()).toBeFalse();
      expect(component.showEditModal()).toBeFalse();
      expect(component.selectedCardForEdit()).toBeNull();
      expect(component.loadCards).toHaveBeenCalled();
      expect(notification.toasts().some(t => t.type === 'success')).toBeTrue();
    });
  });

  describe('Pagination d\'inventaire des cartes', () => {
    const manyCards = Array.from({ length: 35 }, (_, i) => ({
      uid: `04UID${String(i + 1).padStart(4, '0')}`,
      status: i % 3 === 0 ? 'Revoked' : 'Active',
      label: `Usager ${i + 1}`,
      balanceMillimes: (i + 1) * 1000
    }));

    beforeEach(() => {
      component.session.setAdminToken('valid-admin-token');
      apiSpy.getCards.and.callFake((page = 1, pageSize = 10, q?: string, status?: string) => {
        let filtered = manyCards;
        if (q) {
          const upper = q.toUpperCase();
          filtered = filtered.filter(c => c.uid.toUpperCase().includes(upper) || c.label.toUpperCase().includes(upper));
        }
        if (status && status !== 'ALL') {
          filtered = filtered.filter(c => c.status === status);
        }
        const total = filtered.length;
        const pages = Math.max(1, Math.ceil(total / pageSize));
        const start = (page - 1) * pageSize;
        const items = filtered.slice(start, start + pageSize);
        return of({
          items,
          page,
          pageSize,
          totalCount: total,
          totalPages: pages
        });
      });
      component.loadCards();
      fixture.detectChanges();
    });

    it('initialise la pagination à 10 éléments par page et calcule correctement pages et bornes', () => {
      expect(component.pageSize()).toBe(10);
      expect(component.currentPage()).toBe(1);
      expect(component.totalFilteredCards).toBe(35);
      expect(component.totalPages).toBe(4);
      expect(component.startIndex).toBe(1);
      expect(component.endIndex).toBe(10);
      expect(component.paginatedCards.length).toBe(10);
      expect(component.paginatedCards[0].uid).toBe('04UID0001');
      expect(component.paginatedCards[9].uid).toBe('04UID0010');
    });

    it('ne rend dans le tableau DOM que les éléments correspondant à la page courante', () => {
      const rows = fixture.nativeElement.querySelectorAll('tbody tr');
      expect(rows.length).toBe(10);
    });

    it('permet de changer la taille de page (10, 25, 50) et réinitialise la page à 1', () => {
      component.currentPage.set(3);

      component.onPageSizeChange(25);
      expect(component.pageSize()).toBe(25);
      expect(component.currentPage()).toBe(1);
      expect(component.totalPages).toBe(2);
      expect(component.startIndex).toBe(1);
      expect(component.endIndex).toBe(25);
      expect(component.paginatedCards.length).toBe(25);

      component.onPageSizeChange(50);
      expect(component.pageSize()).toBe(50);
      expect(component.currentPage()).toBe(1);
      expect(component.totalPages).toBe(1);
      expect(component.startIndex).toBe(1);
      expect(component.endIndex).toBe(35);
      expect(component.paginatedCards.length).toBe(35);
    });

    it('navigue entre les pages avec mise à jour des bornes et désactivation des limites', () => {
      // Page 1 : première et précédente désactivées
      expect(component.currentPage()).toBe(1);
      fixture.detectChanges();

      const prevBtn: HTMLButtonElement = fixture.nativeElement.querySelector('button[aria-label="Page précédente"]');
      const firstBtn: HTMLButtonElement = fixture.nativeElement.querySelector('button[aria-label="Première page"]');
      const nextBtn: HTMLButtonElement = fixture.nativeElement.querySelector('button[aria-label="Page suivante"]');
      const lastBtn: HTMLButtonElement = fixture.nativeElement.querySelector('button[aria-label="Dernière page"]');

      expect(firstBtn.disabled).toBeTrue();
      expect(prevBtn.disabled).toBeTrue();
      expect(nextBtn.disabled).toBeFalse();
      expect(lastBtn.disabled).toBeFalse();

      // Page suivante
      component.goToNextPage();
      expect(component.currentPage()).toBe(2);
      expect(component.startIndex).toBe(11);
      expect(component.endIndex).toBe(20);
      fixture.detectChanges();
      expect(firstBtn.disabled).toBeFalse();
      expect(prevBtn.disabled).toBeFalse();

      // Dernière page
      component.goToLastPage();
      expect(component.currentPage()).toBe(4);
      expect(component.startIndex).toBe(31);
      expect(component.endIndex).toBe(35);
      expect(component.paginatedCards.length).toBe(5);
      fixture.detectChanges();
      expect(nextBtn.disabled).toBeTrue();
      expect(lastBtn.disabled).toBeTrue();

      // Page précédente
      component.goToPreviousPage();
      expect(component.currentPage()).toBe(3);

      // Première page
      component.goToFirstPage();
      expect(component.currentPage()).toBe(1);
    });

    it('réinitialise la page courante à 1 lors d\'une recherche ou d\'un changement de filtre', () => {
      component.currentPage.set(3);
      expect(component.currentPage()).toBe(3);

      component.onSearchInput('Usager 1');
      expect(component.currentPage()).toBe(1);

      component.currentPage.set(2);
      component.onStatusFilterChange('Revoked');
      expect(component.currentPage()).toBe(1);
    });

    it('corrige la page courante si un filtre réduit le nombre de pages en deçà de la page active', () => {
      component.currentPage.set(4);
      expect(component.currentPage()).toBe(4);

      // Filtre réduisant le total à 1 seule carte
      component.searchFilter.set('04UID0001');
      component.loadCards();
      expect(component.paginatedCards.length).toBe(1);
      expect(component.currentPage()).toBe(1);
    });

    it('distingue l\'état vide global de l\'état vide par critères de filtrage', () => {
      // 1. Filtrage sans résultat
      component.searchFilter.set('UID_INEXISTANT_XYZ');
      component.loadCards();
      fixture.detectChanges();
      const emptyStateText = fixture.nativeElement.querySelector('.empty-state p')?.textContent;
      expect(emptyStateText).toContain('Aucune carte ne correspond aux critères de recherche');
      expect(fixture.nativeElement.querySelector('.table-container')).toBeFalsy();

      // 2. Inventaire complètement vide
      component.resetFilters();
      component.cards.set([]);
      component.totalCardsCount.set(0);
      fixture.detectChanges();
      const globalEmptyText = fixture.nativeElement.querySelector('.empty-state p')?.textContent;
      expect(globalEmptyText).toContain('Aucune carte enregistrée dans le système');
    });
  });

  describe('Couverture avancée des méthodes et branches conditionnelles', () => {
    it('gère le rechargement rapide avec addAmountMillimes et refuse les soldes négatifs ou motifs vides', () => {
      component.session.setAdminToken('valid-admin-token');
      component.openEditModal(mockCards[0]);
      expect(component.editBalanceMillimes).toBe(10000);

      component.addAmountMillimes(5000);
      expect(component.editBalanceMillimes).toBe(15000);

      component.addAmountMillimes(10000);
      expect(component.editBalanceMillimes).toBe(25000);

      // Rejet solde négatif
      component.editBalanceMillimes = -500;
      component.editReason = 'Test solde négatif';
      component.submitCardUpdate();
      expect(component.modalErrorMessage()).toContain('Le solde d\'une carte ne peut pas être négatif');
      expect(apiSpy.upsertCard).not.toHaveBeenCalled();

      // Rejet motif vide
      component.editBalanceMillimes = 10000;
      component.editReason = '   ';
      component.submitCardUpdate();
      expect(component.modalErrorMessage()).toContain('Un motif explicite est obligatoire');
      expect(apiSpy.upsertCard).not.toHaveBeenCalled();
    });

    it('navigue entre les pages serveur en mode ADMIN et vérifie les bornes', () => {
      component.session.setAdminToken('valid-admin-token');
      apiSpy.getCards.and.returnValue(of({
        items: mockCards,
        page: 1,
        pageSize: 10,
        totalCount: 40,
        totalPages: 4
      }));

      component.loadCards();
      expect(component.currentPage()).toBe(1);
      expect(component.totalPages).toBe(4);

      // goToFirstPage quand déjà sur page 1 ne fait rien
      apiSpy.getCards.calls.reset();
      component.goToFirstPage();
      expect(apiSpy.getCards).not.toHaveBeenCalled();

      // goToNextPage avance à 2 et recharge
      component.goToNextPage();
      expect(component.currentPage()).toBe(2);
      expect(apiSpy.getCards).toHaveBeenCalledWith(2, 10, undefined, undefined);

      // goToLastPage saute à la dernière page (4)
      component.goToLastPage();
      expect(component.currentPage()).toBe(4);
      expect(apiSpy.getCards).toHaveBeenCalledWith(4, 10, undefined, undefined);

      // goToLastPage quand déjà sur dernière page ne fait rien
      apiSpy.getCards.calls.reset();
      component.goToLastPage();
      expect(apiSpy.getCards).not.toHaveBeenCalled();

      // goToNextPage quand sur dernière page ne fait rien
      component.goToNextPage();
      expect(component.currentPage()).toBe(4);
      expect(apiSpy.getCards).not.toHaveBeenCalled();

      // goToPreviousPage recule à 3
      component.goToPreviousPage();
      expect(component.currentPage()).toBe(3);
      expect(apiSpy.getCards).toHaveBeenCalledWith(3, 10, undefined, undefined);

      // goToFirstPage retourne à 1
      component.goToFirstPage();
      expect(component.currentPage()).toBe(1);
      expect(apiSpy.getCards).toHaveBeenCalledWith(1, 10, undefined, undefined);

      // goToPreviousPage quand sur page 1 ne fait rien
      apiSpy.getCards.calls.reset();
      component.goToPreviousPage();
      expect(apiSpy.getCards).not.toHaveBeenCalled();
    });

    it('permet de modifier la taille de page parmi les valeurs autorisées (10, 25, 50)', () => {
      component.session.setAdminToken('valid-admin-token');
      apiSpy.getCards.calls.reset();

      component.onPageSizeChange(50);
      expect(component.pageSize()).toBe(50);
      expect(component.currentPage()).toBe(1);
      expect(apiSpy.getCards).toHaveBeenCalledWith(1, 50, undefined, undefined);

      component.onPageSizeChange(25);
      expect(component.pageSize()).toBe(25);

      // Valeur non autorisée ignorée
      apiSpy.getCards.calls.reset();
      component.onPageSizeChange(999);
      expect(component.pageSize()).toBe(25);
      expect(apiSpy.getCards).not.toHaveBeenCalled();
    });

    it('gère l\'affichage, les erreurs et la fermeture de la fiche détaillée', () => {
      component.session.setAdminToken('valid-admin-token');
      const mockDetail: CardDetail = {
        uid: '04A1B2C3D4E5F6',
        status: 'Active',
        label: 'Carte Détail Test',
        balanceMillimes: 12000,
        transactionsCount: 1,
        limit: 50,
        offset: 0,
        transactions: []
      };
      apiSpy.getCard.and.returnValue(of(mockDetail));

      component.openCardDetail(mockCards[0]);
      expect(component.selectedCardDetail()?.label).toBe('Carte Détail Test');
      expect(component.isLoadingDetail()).toBeFalse();

      component.closeCardDetail();
      expect(component.selectedCardDetail()).toBeNull();

      // Erreur getCard fournit un objet de repli
      apiSpy.getCard.and.returnValue(throwError(() => new Error('Erreur réseau')));
      component.openCardDetail(mockCards[0]);
      expect(component.selectedCardDetail()?.uid).toBe(mockCards[0].uid);
      expect(component.isLoadingDetail()).toBeFalse();
    });

    it('gère l\'erreur réseau lors du chargement des cartes en mode ADMIN et TERMINAL', () => {
      // 1. Erreur en mode ADMIN
      component.session.setAdminToken('valid-admin-token');
      apiSpy.getCards.and.returnValue(throwError(() => new Error('API indisponible')));

      component.loadCards();
      expect(component.cards()).toEqual([]);
      expect(component.totalCardsCount()).toBe(0);
      expect(component.totalPagesCount()).toBe(1);
      expect(component.isLoading()).toBeFalse();

      // 2. Erreur en mode TERMINAL
      component.session.clearTokens();
      component.session.setTerminalToken('valid-term-token');
      apiSpy.getSimulatorCards.and.returnValue(throwError(() => new Error('Borne déconnectée')));

      component.loadCards();
      expect(component.cards()).toEqual([]);
      expect(component.totalCardsCount()).toBe(0);
      expect(component.totalPagesCount()).toBe(1);
      expect(component.isLoading()).toBeFalse();
    });

    it('borne la page courante au maximum disponible via clampCurrentPage', () => {
      component.currentPage.set(10);
      (component as unknown as { clampCurrentPage: () => void }).clampCurrentPage();
      expect(component.currentPage()).toBe(component.totalPages);
    });

    it('gère le clic backdrop et le piégeage du focus clavier sur la modale', () => {
      component.session.setAdminToken('valid-admin-token');
      component.openEditModal(mockCards[0]);
      expect(component.showEditModal()).toBeTrue();

      // Clic hors de la boîte de dialogue ferme la modale
      const fakeEvent = {
        target: 'backdrop',
        currentTarget: 'backdrop'
      } as unknown as MouseEvent;
      component.onBackdropClick(fakeEvent);
      expect(component.showEditModal()).toBeFalse();

      // Si isSubmitting est vrai, onBackdropClick ne ferme pas
      component.openEditModal(mockCards[0]);
      component.isSubmitting.set(true);
      component.onBackdropClick(fakeEvent);
      expect(component.showEditModal()).toBeTrue();
      component.isSubmitting.set(false);

      // onTabTrap avec touche autre que Tab ne fait rien
      const nonTabEvent = new KeyboardEvent('keydown', { key: 'Escape' });
      expect(() => component.onTabTrap(nonTabEvent)).not.toThrow();
    });

    it('ferme la fiche détaillée lors de l\'appui sur la touche Escape', () => {
      const mockDetail: CardDetail = {
        uid: '04A1B2C3D4E5F6',
        status: 'Active',
        label: 'Carte Test',
        balanceMillimes: 10000,
        transactionsCount: 0,
        limit: 50,
        offset: 0,
        transactions: []
      };
      component.selectedCardDetail.set(mockDetail);
      expect(component.selectedCardDetail()).not.toBeNull();

      const escapeEvent = new KeyboardEvent('keydown', { key: 'Escape' });
      spyOn(escapeEvent, 'preventDefault');
      component.onEscapeKey(escapeEvent);

      expect(escapeEvent.preventDefault).toHaveBeenCalled();
      expect(component.selectedCardDetail()).toBeNull();
    });

    it('actualise la fiche détaillée ouverte après une mise à jour réussie de carte', () => {
      component.session.setAdminToken('valid-admin-token');
      apiSpy.upsertCard.and.returnValue(of({
        uid: '04A1B2C3D4E5F6',
        status: 'Active',
        label: 'Carte Renommée',
        balanceMillimes: 20000
      }));
      apiSpy.getCard.and.returnValue(of({
        uid: '04A1B2C3D4E5F6',
        status: 'Active',
        label: 'Carte Renommée',
        balanceMillimes: 20000,
        transactionsCount: 0,
        limit: 50,
        offset: 0,
        transactions: []
      }));

      // Ouvrir fiche détaillée pour la même carte
      component.openCardDetail(mockCards[0]);
      expect(component.selectedCardDetail()?.uid).toBe('04A1B2C3D4E5F6');

      // Ouvrir modale et soumettre
      component.openEditModal(mockCards[0]);
      component.editLabel = 'Carte Renommée';
      component.editReason = 'Mise à jour motif';
      component.submitCardUpdate();

      expect(apiSpy.upsertCard).toHaveBeenCalled();
      expect(apiSpy.getCard).toHaveBeenCalled();
      expect(component.selectedCardDetail()?.label).toBe('Carte Renommée');
    });

    it('ajuste la page courante en mode TERMINAL si le nombre de pages calculé est inférieur', () => {
      component.session.setTerminalToken('valid-term-token');
      apiSpy.getSimulatorCards.and.returnValue(of([
        { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte Unique' }
      ]));

      // On force currentPage à 5 avant le chargement
      component.currentPage.set(5);
      component.loadCards();

      expect(component.totalCardsCount()).toBe(1);
      expect(component.totalPagesCount()).toBe(1);
      expect(component.currentPage()).toBe(1);
    });

    it('exécute le piégeage du focus au clavier (Tab et Shift+Tab) dans la modale', () => {
      component.session.setAdminToken('valid-admin-token');
      component.openEditModal(mockCards[0]);
      fixture.detectChanges();

      const modalDialog = fixture.nativeElement.querySelector('.modal-dialog[role="dialog"]');
      expect(modalDialog).toBeTruthy();

      const focusables = Array.from(
        modalDialog.querySelectorAll('button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled])')
      ) as HTMLElement[];

      if (focusables.length >= 2) {
        const first = focusables[0];
        const last = focusables[focusables.length - 1];

        // Shift+Tab sur le premier élément boucle vers le dernier
        first.focus();
        const shiftTabEvent = new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true });
        spyOn(shiftTabEvent, 'preventDefault');
        component.onTabTrap(shiftTabEvent);

        // Tab sur le dernier élément boucle vers le premier
        last.focus();
        const tabEvent = new KeyboardEvent('keydown', { key: 'Tab', shiftKey: false });
        spyOn(tabEvent, 'preventDefault');
        component.onTabTrap(tabEvent);
      }
    });

    it('traite la saisie de recherche temporisée avec debounceTime via onSearchInput', fakeAsync(() => {
      component.session.setAdminToken('valid-admin-token');
      apiSpy.getCards.calls.reset();

      component.onSearchInput('Usager');
      expect(component.currentPage()).toBe(1);
      expect(component.searchFilter()).toBe(''); // pas encore émis avant 300ms

      tick(300);
      expect(component.searchFilter()).toBe('Usager');
      expect(apiSpy.getCards).toHaveBeenCalled();
    }));
  });
});
