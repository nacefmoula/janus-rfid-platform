import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { provideRouter } from '@angular/router';
import { provideHttpClient, HttpErrorResponse } from '@angular/common/http';
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

    // Valeurs de repli sûres des compteurs lorsque les métriques sont absentes
    expect(component.acceptanceRate).toBe(0);
    expect(component.activeCardsCount).toBe(0);
    expect(component.revokedCardsCount).toBe(0);
    expect(component.totalCardsCount).toBe(0);
    expect(component.totalCirculatingBalanceMillimes).toBe(0);

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

  it('affiche le libellé exact TOTAL DES DÉBITS ENREGISTRÉS et la base PostgreSQL 18 dans l\'infrastructure', () => {
    fixture.detectChanges();

    const revenueTitle: HTMLElement | null = fixture.nativeElement.querySelector('.kpi-card.border-primary .kpi-title');
    expect(revenueTitle?.textContent).toContain('TOTAL DES DÉBITS ENREGISTRÉS');

    const revenueFooter: HTMLElement | null = fixture.nativeElement.querySelector('.kpi-card.border-primary .kpi-footer');
    expect(revenueFooter?.textContent).toContain('Consolidé depuis PostgreSQL');
    expect(revenueFooter?.textContent).toContain('FareTransactions');

    const systemRows = fixture.nativeElement.querySelectorAll('.system-item');
    const systemTexts = Array.from(systemRows).map((el: any) => el.textContent);
    expect(systemTexts.some((t: string) => t.includes('Base relationnelle (PostgreSQL 18)'))).toBeTrue();
  });

  it('affiche un statut PostgreSQL initialement non vérifié en mode ANONYMOUS sans pastille verte ni erreur', () => {
    fixture.detectChanges();
    expect(component.postgresStatus.state).toBe('UNVERIFIED');
    expect(component.postgresStatus.isOperational).toBeFalse();
    expect(component.postgresStatus.isError).toBeFalse();
    expect(component.postgresStatus.label).toContain('Non vérifié (session requise)');

    const systemRows = fixture.nativeElement.querySelectorAll('.system-item');
    const pgRow = Array.from(systemRows).find((el: any) => el.textContent.includes('PostgreSQL 18')) as HTMLElement;
    expect(pgRow).toBeTruthy();
    expect(pgRow.textContent).toContain('Non vérifié (session requise)');

    const indicator = pgRow.querySelector('.status-indicator');
    expect(indicator?.classList.contains('on')).toBeFalse();
    expect(indicator?.classList.contains('error')).toBeFalse();
  });

  it('passe de la vérification en cours à l\'état opérationnel après une réponse SQL réussie', () => {
    const summarySubject = new Subject<PrometheusMetricsSummary>();
    apiSpy.getMetricsSummary.and.returnValue(summarySubject.asObservable());

    component.session.setTerminalToken('token-borne-123');
    fixture.detectChanges();

    // En cours de chargement initial
    expect(component.isLoading()).toBeTrue();
    expect(component.postgresStatus.state).toBe('CHECKING');
    expect(component.postgresStatus.label).toBe('Vérification en cours...');
    expect(component.postgresStatus.isOperational).toBeFalse();

    const systemRowsBefore = fixture.nativeElement.querySelectorAll('.system-item');
    const pgRowBefore = Array.from(systemRowsBefore).find((el: any) => el.textContent.includes('PostgreSQL 18')) as HTMLElement;
    const indicatorBefore = pgRowBefore.querySelector('.status-indicator');
    expect(indicatorBefore?.classList.contains('on')).toBeFalse();

    // Réception du succès SQL
    summarySubject.next(mockSummary);
    fixture.detectChanges();

    expect(component.isLoading()).toBeFalse();
    expect(component.postgresStatus.state).toBe('OPERATIONAL');
    expect(component.postgresStatus.label).toBe('Opérationnel (vérifié via SQL)');
    expect(component.postgresStatus.isOperational).toBeTrue();

    const pgRowAfter = Array.from(fixture.nativeElement.querySelectorAll('.system-item')).find((el: any) => el.textContent.includes('PostgreSQL 18')) as HTMLElement;
    const indicatorAfter = pgRowAfter.querySelector('.status-indicator');
    expect(indicatorAfter?.classList.contains('on')).toBeTrue();
    expect(indicatorAfter?.classList.contains('error')).toBeFalse();
  });

  it('bascule vers l\'état indisponible en cas d\'erreur de base de données (HTTP 500) sans aucun état vert', () => {
    apiSpy.getMetricsSummary.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 500,
      statusText: 'Internal Server Error',
      error: { error: 'database_unavailable' }
    })));

    component.session.setAdminToken('token-admin-123');
    fixture.detectChanges();

    expect(component.metrics()).toBeNull();
    expect(component.isLoading()).toBeFalse();
    expect(component.postgresStatus.state).toBe('UNAVAILABLE');
    expect(component.postgresStatus.label).toBe('Indisponible (échec requête SQL)');
    expect(component.postgresStatus.isOperational).toBeFalse();
    expect(component.postgresStatus.isError).toBeTrue();

    const pgRow = Array.from(fixture.nativeElement.querySelectorAll('.system-item')).find((el: any) => el.textContent.includes('PostgreSQL 18')) as HTMLElement;
    const indicator = pgRow.querySelector('.status-indicator');
    expect(indicator?.classList.contains('on')).toBeFalse();
    expect(indicator?.classList.contains('error')).toBeTrue();
    expect(pgRow.textContent).toContain('Indisponible (échec requête SQL)');
  });

  it('ne déclare pas PostgreSQL indisponible en cas de droits insuffisants (HTTP 403)', () => {
    apiSpy.getMetricsSummary.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 403,
      statusText: 'Forbidden'
    })));

    component.session.setTerminalToken('token-restricted-123');
    fixture.detectChanges();

    expect(component.postgresStatus.state).toBe('UNVERIFIED');
    expect(component.postgresStatus.label).toBe('Non vérifié (droits insuffisants)');
    expect(component.postgresStatus.isOperational).toBeFalse();
    expect(component.postgresStatus.isError).toBeFalse();

    const pgRow = Array.from(fixture.nativeElement.querySelectorAll('.system-item')).find((el: any) => el.textContent.includes('PostgreSQL 18')) as HTMLElement;
    const indicator = pgRow.querySelector('.status-indicator');
    expect(indicator?.classList.contains('on')).toBeFalse();
    expect(indicator?.classList.contains('error')).toBeFalse();
  });

  it('réinitialise immédiatement le statut PostgreSQL lors de la déconnexion ou changement de rôle', () => {
    component.session.setTerminalToken('token-borne-123');
    fixture.detectChanges();

    expect(component.postgresStatus.state).toBe('OPERATIONAL');
    expect(component.postgresStatus.isOperational).toBeTrue();

    // Déconnexion
    component.session.clearTokens();
    fixture.detectChanges();

    expect(component.postgresStatus.state).toBe('UNVERIFIED');
    expect(component.postgresStatus.label).toBe('Non vérifié (session requise)');
    expect(component.postgresStatus.isOperational).toBeFalse();
    expect(component.postgresStatus.isError).toBeFalse();

    const pgRow = Array.from(fixture.nativeElement.querySelectorAll('.system-item')).find((el: any) => el.textContent.includes('PostgreSQL 18')) as HTMLElement;
    const indicator = pgRow.querySelector('.status-indicator');
    expect(indicator?.classList.contains('on')).toBeFalse();
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
