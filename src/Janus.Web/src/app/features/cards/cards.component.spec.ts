import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { CardsComponent } from './cards.component';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { CardDetail } from '../../core/models/card.model';

describe('CardsComponent', () => {
  let component: CardsComponent;
  let fixture: ComponentFixture<CardsComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;

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
    fixture.detectChanges();
  });

  it('devrait créer le composant et lister les cartes', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getDemoCards).toHaveBeenCalled();
    expect(component.cards().length).toBe(2);
  });

  it('devrait utiliser getSimulatorCards lorsque le rôle authentifié est TERMINAL sans appeler getDemoCards', () => {
    apiSpy.getDemoCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.session.setTerminalToken('valid-terminal-session-token');
    component.loadCards();

    expect(apiSpy.getSimulatorCards).toHaveBeenCalledTimes(1);
    expect(apiSpy.getDemoCards).not.toHaveBeenCalled();
    expect(component.cards().length).toBe(2);
    expect(component.cards()[0].balanceMillimes).toBeUndefined();
  });

  it('devrait utiliser getDemoCards lorsque le rôle authentifié est ADMIN', () => {
    apiSpy.getDemoCards.calls.reset();
    apiSpy.getSimulatorCards.calls.reset();

    component.session.setAdminToken('valid-admin-session-token');
    component.loadCards();

    expect(apiSpy.getDemoCards).toHaveBeenCalledTimes(1);
    expect(apiSpy.getSimulatorCards).not.toHaveBeenCalled();
    expect(component.cards()[0].balanceMillimes).toBe(10000);
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
});
