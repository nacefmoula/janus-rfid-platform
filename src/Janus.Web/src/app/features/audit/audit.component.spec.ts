import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { AuditComponent } from './audit.component';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { NotificationService } from '../../core/services/notification.service';
import { CardAuditResponse } from '../../core/models/card.model';

describe('AuditComponent', () => {
  let component: AuditComponent;
  let fixture: ComponentFixture<AuditComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;
  let notification: NotificationService;

  const mockCards = [
    { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte 1', balanceMillimes: 10000 }
  ];

  const mockAuditResponse: CardAuditResponse = {
    cardUid: '04A1B2C3D4E5F6',
    totalCount: 1,
    limit: 50,
    offset: 0,
    auditLogs: [
      {
        id: 1,
        action: 'ADMIN_CARD_UPDATED',
        previousBalanceMillimes: 10000,
        newBalanceMillimes: 15000,
        previousStatus: 'Active',
        newStatus: 'Active',
        reason: 'Recharge 5 TND',
        actor: 'ADMIN',
        createdAt: '2026-10-16T10:00:00Z'
      }
    ]
  };

  beforeEach(async () => {
    apiSpy = jasmine.createSpyObj('JanusApiService', ['getDemoCards', 'getCardAudit']);
    apiSpy.getDemoCards.and.returnValue(of(mockCards));
    apiSpy.getCardAudit.and.returnValue(of(mockAuditResponse));

    await TestBed.configureTestingModule({
      imports: [AuditComponent],
      providers: [
        { provide: JanusApiService, useValue: apiSpy },
        SessionService,
        NotificationService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(AuditComponent);
    component = fixture.componentInstance;
    notification = TestBed.inject(NotificationService);
    fixture.detectChanges();
  });

  it('devrait bloquer les requêtes et afficher l\'écran de verrouillage RBAC en mode ANONYMOUS', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getDemoCards).not.toHaveBeenCalled();
    expect(apiSpy.getCardAudit).not.toHaveBeenCalled();
    expect(component.auditData()).toBeNull();

    const barrier = fixture.nativeElement.querySelector('.rbac-barrier-panel');
    expect(barrier).toBeTruthy();
    expect(barrier.textContent).toContain('Visiteur (Anonyme)');

    const tagVal = fixture.nativeElement.querySelector('.rbac-status-tag .tag-val');
    expect(tagVal?.textContent).toContain('Visiteur non authentifié (401)');
  });

  it('devrait bloquer les requêtes et afficher la barrière RBAC adaptée en mode TERMINAL', () => {
    component.session.setTerminalToken('token-borne-123');
    fixture.detectChanges();

    expect(apiSpy.getDemoCards).not.toHaveBeenCalled();
    expect(apiSpy.getCardAudit).not.toHaveBeenCalled();
    expect(component.auditData()).toBeNull();

    const barrier = fixture.nativeElement.querySelector('.rbac-barrier-panel');
    expect(barrier).toBeTruthy();
    expect(barrier.textContent).toContain('Opérateur Borne');

    const tagVal = fixture.nativeElement.querySelector('.rbac-status-tag .tag-val');
    expect(tagVal?.textContent).toContain('Opérateur Borne (403 interdit)');
  });

  it('devrait charger les cartes et l\'audit de la première carte en mode ADMIN et déverrouiller la vue', () => {
    component.session.setAdminToken('valid-admin-token');
    fixture.detectChanges();

    expect(apiSpy.getDemoCards).toHaveBeenCalledTimes(1);
    expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04A1B2C3D4E5F6', 1, 25);
    expect(component.auditData()).toEqual(mockAuditResponse);

    const barrier = fixture.nativeElement.querySelector('.rbac-barrier-panel');
    expect(barrier).toBeFalsy();

    const tagVal = fixture.nativeElement.querySelector('.rbac-status-tag .tag-val');
    expect(tagVal?.textContent).toContain('Accès Administrateur actif');
  });

  it('devrait afficher l\'erreur RBAC 403 lorsque l\'API refuse l\'accès sans émettre de double toast composant', () => {
    component.session.setAdminToken('valid-admin-token');
    fixture.detectChanges();

    apiSpy.getCardAudit.and.returnValue(throwError(() => ({
      status: 403,
      error: { error: 'forbidden', message: 'Terminal credentials cannot view administrative audit logs.' }
    })));

    component.fetchAuditLogs();

    expect(component.auditData()).toBeNull();
    expect(component.rbacError()).toBeTruthy();
    expect(component.rbacError()?.status).toBe(403);
    expect(component.rbacError()?.message).toContain('Opérateur Borne ne peut pas consulter les journaux');
    expect(notification.toasts().length).toBe(0);
  });

  it('purge les journaux d\'audit lors de clearTokens() et ignore toute réponse tardive de l\'ancienne session', () => {
    component.session.setAdminToken('valid-admin-token');
    fixture.detectChanges();
    expect(component.auditData()).not.toBeNull();

    const delayedAudit$ = new Subject<CardAuditResponse>();
    apiSpy.getCardAudit.and.returnValue(delayedAudit$.asObservable());

    component.fetchAuditLogs();
    expect(component.isLoading()).toBeTrue();

    component.session.clearTokens();
    fixture.detectChanges();

    expect(component.auditData()).toBeNull();
    expect(component.demoCards()).toEqual([]);
    expect(component.isLoading()).toBeFalse();
    expect(component.currentPage()).toBe(1);
    expect(component.totalAuditCount()).toBe(0);
    expect(fixture.nativeElement.querySelector('.rbac-barrier-panel')).toBeTruthy();

    // L'ancienne requête d'audit termine après la déconnexion : elle ne doit pas réafficher les données
    delayedAudit$.next(mockAuditResponse);
    expect(component.auditData()).toBeNull();
  });

  describe('Pagination serveur du journal d\'audit', () => {
    const multiPageAuditResponse: CardAuditResponse = {
      cardUid: '04A1B2C3D4E5F6',
      totalCount: 30,
      limit: 10,
      offset: 0,
      page: 1,
      pageSize: 10,
      totalPages: 3,
      items: Array.from({ length: 10 }, (_, i) => ({
        id: i + 1,
        action: 'ADMIN_CARD_UPDATED',
        previousBalanceMillimes: 10000,
        newBalanceMillimes: 15000,
        previousStatus: 'Active',
        newStatus: 'Active',
        reason: `Recharge ${i + 1}`,
        actor: 'ADMIN',
        createdAt: '2026-10-16T10:00:00Z'
      })),
      auditLogs: []
    };

    beforeEach(() => {
      component.session.setAdminToken('valid-admin-token');
      apiSpy.getCardAudit.and.returnValue(of(multiPageAuditResponse));
      component.selectedUid.set('04A1B2C3D4E5F6');
      component.pageSize.set(10);
      component.fetchAuditLogs();
      fixture.detectChanges();
    });

    it('calcule correctement les bornes et pages de l\'audit', () => {
      expect(component.totalAuditCount()).toBe(30);
      expect(component.totalPages()).toBe(3);
      expect(component.startIndex).toBe(1);
      expect(component.endIndex).toBe(10);
    });

    it('navigue entre les pages et déclenche une requête paginée avec les paramètres corrects', () => {
      apiSpy.getCardAudit.calls.reset();

      component.goToNextPage();
      expect(component.currentPage()).toBe(2);
      expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04A1B2C3D4E5F6', 2, 10);

      component.goToLastPage();
      expect(component.currentPage()).toBe(3);
      expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04A1B2C3D4E5F6', 3, 10);

      component.goToPreviousPage();
      expect(component.currentPage()).toBe(2);
      expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04A1B2C3D4E5F6', 2, 10);

      component.goToFirstPage();
      expect(component.currentPage()).toBe(1);
      expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04A1B2C3D4E5F6', 1, 10);
    });

    it('modifie la taille de page et réinitialise à la première page', () => {
      apiSpy.getCardAudit.calls.reset();

      component.currentPage.set(2);
      component.onPageSizeChange(25);

      expect(component.pageSize()).toBe(25);
      expect(component.currentPage()).toBe(1);
      expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04A1B2C3D4E5F6', 1, 25);
    });

    it('réinitialise la page à 1 lors du changement de carte sélectionnée', () => {
      apiSpy.getCardAudit.calls.reset();

      component.currentPage.set(3);
      component.onCardSelected('04DEAD00000001');

      expect(component.selectedUid()).toBe('04DEAD00000001');
      expect(component.currentPage()).toBe(1);
      expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04DEAD00000001', 1, 10);
    });
  });
});
