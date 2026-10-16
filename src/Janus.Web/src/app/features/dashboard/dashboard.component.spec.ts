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
    apiSpy.getDemoCards.and.returnValue(of([
      { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte démo 1', balanceMillimes: 10000 },
      { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte démo 2', balanceMillimes: 5000 }
    ]));
    apiSpy.getMetricsSummary.and.returnValue(of({
      totalValidations: 20,
      acceptedValidations: 16,
      rejectedValidations: 4,
      conflictValidations: 1,
      totalDebitedMillimes: 24000,
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

  it('devrait créer le composant dashboard et charger les métriques initiales', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getHealth).toHaveBeenCalled();
    expect(apiSpy.getDemoCards).toHaveBeenCalled();
    expect(apiSpy.getMetricsSummary).toHaveBeenCalled();
  });

  it('devrait calculer correctement le taux d\'acceptation des validations', () => {
    // 16 / 20 = 80%
    expect(component.acceptanceRate).toBe(80);
  });

  it('devrait compter fidèlement les cartes actives et révoquées', () => {
    expect(component.activeCardsCount).toBe(1);
    expect(component.revokedCardsCount).toBe(1);
    expect(component.totalCirculatingBalanceMillimes).toBe(15000);
  });
});
