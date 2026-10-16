import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, throwError } from 'rxjs';
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
    fixture.detectChanges();
  });

  it('devrait initialiser le composant et charger les journaux d\'audit de la première carte', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getDemoCards).toHaveBeenCalled();
    expect(apiSpy.getCardAudit).toHaveBeenCalledWith('04A1B2C3D4E5F6');
    expect(component.auditData()).toEqual(mockAuditResponse);
  });

  it('devrait afficher l\'erreur RBAC 403 lorsque les droits administrateur sont refusés', () => {
    apiSpy.getCardAudit.and.returnValue(throwError(() => ({
      status: 403,
      error: { error: 'forbidden', message: 'Terminal credentials cannot view administrative audit logs.' }
    })));

    component.fetchAuditLogs();

    expect(component.auditData()).toBeNull();
    expect(component.rbacError()).toBeTruthy();
    expect(component.rbacError()?.status).toBe(403);
    expect(component.rbacError()?.message).toContain('Opérateur Borne ne peut pas consulter les journaux');
  });
});
