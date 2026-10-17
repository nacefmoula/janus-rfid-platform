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

  it('devrait créer le composant et ne pas appeler getMetricsSummary en mode ANONYMOUS', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getHealth).toHaveBeenCalled();
    expect(apiSpy.getMetricsSummary).not.toHaveBeenCalled();
    expect(apiSpy.getDemoCards).not.toHaveBeenCalled();
    expect(component.isLoading()).toBeFalse();
    expect(component.metrics()).toBeNull();

    const banner: HTMLElement | null = fixture.nativeElement.querySelector('.anonymous-session-banner');
    expect(banner).toBeTruthy();
    expect(banner?.textContent).toContain('Session requise');
  });

  it('devrait charger les métriques après connexion Terminal et masquer la bannière anonyme', () => {
    component.session.setTerminalToken('token-borne-123');
    fixture.detectChanges();

    expect(apiSpy.getMetricsSummary).toHaveBeenCalledTimes(1);
    expect(component.metrics()).toEqual(mockSummary);
    expect(component.isLoading()).toBeFalse();

    const banner: HTMLElement | null = fixture.nativeElement.querySelector('.anonymous-session-banner');
    expect(banner).toBeFalsy();
  });

  it('devrait calculer fidèlement le taux d\'acceptation et les compteurs lorsque les métriques sont renseignées', () => {
    component.session.setAdminToken('token-admin-123');
    fixture.detectChanges();

    // 16 / 20 = 80%
    expect(component.acceptanceRate).toBe(80);
    expect(component.activeCardsCount).toBe(1);
    expect(component.revokedCardsCount).toBe(1);
    expect(component.totalCardsCount).toBe(2);
    expect(component.totalCirculatingBalanceMillimes).toBe(15000);
  });

  it('devrait purger sur clearTokens() et ignorer les réponses tardives issues d\'une ancienne session', () => {
    component.session.setTerminalToken('token-borne-123');
    expect(component.metrics()).not.toBeNull();

    const delayedSummary$ = new Subject<PrometheusMetricsSummary>();
    apiSpy.getMetricsSummary.and.returnValue(delayedSummary$.asObservable());

    component.loadDashboardData();
    expect(component.isLoading()).toBeTrue();

    component.session.clearTokens();
    fixture.detectChanges();

    expect(component.metrics()).toBeNull();
    expect(component.isLoading()).toBeFalse();
    expect(fixture.nativeElement.querySelector('.anonymous-session-banner')).toBeTruthy();

    // Une réponse tardive ne doit pas réécraser metrics() après déconnexion
    delayedSummary$.next(mockSummary);
    expect(component.metrics()).toBeNull();
  });
});
