import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { CardsComponent } from './cards.component';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { CardDetail, TransitCard } from '../../core/models/card.model';

describe('CardsComponent', () => {
  let component: CardsComponent;
  let fixture: ComponentFixture<CardsComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;
  let notification: NotificationService;

  const mockCards = [
    { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte Usager Régulier', balanceMillimes: 10000 },
    { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte Opposition', balanceMillimes: 0 }
  ];

  const mockSimulatorCards = [
    { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte Usager Régulier' },
    { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte Opposition' }
  ];

  beforeEach(async () => {
    apiSpy = jasmine.createSpyObj('JanusApiService', ['getDemoCards', 'getSimulatorCards', 'getCard', 'upsertCard']);
    apiSpy.getDemoCards.and.returnValue(of(mockCards));
    apiSpy.getSimulatorCards.and.returnValue(of(mockSimulatorCards));

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
    expect(apiSpy.getDemoCards).toHaveBeenCalled();
    expect(component.cards().length).toBe(2);
  });

  it('7. devrait rafraîchir automatiquement via getSimulatorCards lorsque la session passe à TERMINAL sans appeler getDemoCards', () => {
    apiSpy.getDemoCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.session.setTerminalToken('valid-terminal-session-token');

    expect(apiSpy.getSimulatorCards).toHaveBeenCalledTimes(1);
    expect(apiSpy.getDemoCards).not.toHaveBeenCalled();
    expect(component.cards().length).toBe(2);
    expect(component.cards()[0].balanceMillimes).toBeUndefined();
  });

  it('7. devrait rafraîchir automatiquement via getDemoCards lorsque la session passe à ADMIN', () => {
    apiSpy.getDemoCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.session.setAdminToken('valid-admin-session-token');

    expect(apiSpy.getDemoCards).toHaveBeenCalledTimes(1);
    expect(apiSpy.getSimulatorCards).not.toHaveBeenCalled();
    expect(component.cards()[0].balanceMillimes).toBe(10000);
  });

  it('ne devrait pas déclencher la route terminale getSimulatorCards en rôle ANONYMOUS même si un jeton terminal réside encore en mémoire', () => {
    component.session.configureSession({
      terminalToken: 'residual-terminal-token',
      adminToken: null,
      role: 'ANONYMOUS'
    });
    apiSpy.getDemoCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.loadCards();

    expect(apiSpy.getSimulatorCards).not.toHaveBeenCalled();
    expect(apiSpy.getDemoCards).toHaveBeenCalledTimes(1);
  });

  it('devrait filtrer les cartes par UID ou libellé de recherche', () => {
    component.searchFilter.set('DEAD');
    expect(component.filteredCards.length).toBe(1);
    expect(component.filteredCards[0].uid).toBe('04DEAD00000001');

    component.searchFilter.set('Régulier');
    expect(component.filteredCards.length).toBe(1);
    expect(component.filteredCards[0].uid).toBe('04A1B2C3D4E5F6');
  });

  it('devrait filtrer les cartes par statut d\'exploitation', () => {
    component.statusFilter.set('Active');
    expect(component.filteredCards.length).toBe(1);
    expect(component.filteredCards[0].status).toBe('Active');

    component.statusFilter.set('Revoked');
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

  it('devrait soumettre la mise à jour d\'une carte avec motif d\'audit obligatoire', () => {
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
    const delayedAdminCards$ = new Subject<TransitCard[]>();
    apiSpy.getDemoCards.and.returnValue(delayedAdminCards$.asObservable());

    // Déclenche une requête en cours sous l'ancienne session
    component.loadCards();
    expect(component.isLoading()).toBeTrue();

    // Changement de session vers ANONYMOUS avant la réponse
    component.session.clearTokens();
    expect(component.cards()).toEqual([]);
    expect(component.isLoading()).toBeFalse();

    // L'ancienne réponse arrive enfin : elle doit être ignorée
    delayedAdminCards$.next(mockCards);
    expect(component.cards()).toEqual([]);
  });

  it('10. n\'émet pas de toast d\'erreur en doublon depuis le composant lors d\'un échec upsertCard', () => {
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
});
