import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { DashboardComponent } from './dashboard.component';
import { JanusApiService } from '../../core/services/api.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';

describe('DashboardComponent', () => {
  let component: DashboardComponent;
  let fixture: ComponentFixture<DashboardComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;

  beforeEach(async () => {
    apiSpy = jasmine.createSpyObj('JanusApiService', ['getHealth', 'getDemoCards', 'getMetricsSummary']);
    apiSpy.getHealth.and.returnValue(of({ status: 'healthy', service: 'janus-api' }));
    apiSpy.getMetricsSummary.and.returnValue(of({
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
    }));

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
});
