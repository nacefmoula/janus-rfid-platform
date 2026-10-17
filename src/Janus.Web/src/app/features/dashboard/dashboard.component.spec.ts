import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject } from 'rxjs';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { DashboardComponent } from './dashboard.component';
import { JanusApiService } from '../../core/services/api.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';
import { PrometheusMetricsSummary } from '../../core/models/health.model';

describe('DashboardComponent', () => {
  let component: DashboardComponent;
  let fixture: ComponentFixture<DashboardComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;

  const mockSummary: PrometheusMetricsSummary = {
    totalValidations: 20,
    acceptedValidations: 16,
    rejectedValidations: 4,
    conflictValidations: 1,
    totalDebitedMillimes: 24000,
    activeCardsCount: 1,
    revokedCardsCount: 1,
    totalCardsCount: 2,
    totalCirculatingBalanceMillimes: 15000,
    scrapedAt: new Date()
  };

  beforeEach(async () => {
    apiSpy = jasmine.createSpyObj('JanusApiService', ['getHealth', 'getDemoCards', 'getMetricsSummary']);
    apiSpy.getHealth.and.returnValue(of({ status: 'healthy', service: 'janus-api' }));
    apiSpy.getMetricsSummary.and.returnValue(of(mockSummary));

    await TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        { provide: JanusApiService, useValue: apiSpy },
        ValidationSessionService,
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DashboardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('devrait créer le composant dashboard et charger la synthèse sans appeler la route AdminOnly getDemoCards', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getHealth).toHaveBeenCalled();
    expect(apiSpy.getMetricsSummary).toHaveBeenCalled();
    expect(apiSpy.getDemoCards).not.toHaveBeenCalled();
  });

  it('devrait calculer correctement le taux d\'acceptation des validations', () => {
    // 16 / 20 = 80%
    expect(component.acceptanceRate).toBe(80);
  });

  it('devrait compter fidèlement les cartes actives et révoquées depuis la synthèse', () => {
    expect(component.activeCardsCount).toBe(1);
    expect(component.revokedCardsCount).toBe(1);
    expect(component.totalCardsCount).toBe(2);
    expect(component.totalCirculatingBalanceMillimes).toBe(15000);
  });

  it('6, 7 & 8. rafraîchit automatiquement sur changement de session, purge sur clearTokens() et ignore les réponses tardives', () => {
    apiSpy.getMetricsSummary.calls.reset();
    component.session.setTerminalToken('token-borne-123');
    expect(apiSpy.getMetricsSummary).toHaveBeenCalledTimes(1);

    const delayedSummary$ = new Subject<PrometheusMetricsSummary>();
    apiSpy.getMetricsSummary.and.returnValue(delayedSummary$.asObservable());

    component.loadDashboardData();
    expect(component.isLoading()).toBeTrue();

    component.session.clearTokens();
    expect(component.metrics()).toBeNull();
    expect(component.isLoading()).toBeFalse();

    // Une réponse tardive ne doit pas réécraser metrics() après déconnexion
    delayedSummary$.next(mockSummary);
    expect(component.metrics()).toBeNull();
  });
});
