import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { SimulatorComponent } from './simulator.component';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';
import { ValidationResult } from '../../core/models/validation.model';

describe('SimulatorComponent', () => {
  let component: SimulatorComponent;
  let fixture: ComponentFixture<SimulatorComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;
  let sessionService: SessionService;
  let validationSession: ValidationSessionService;

  beforeEach(async () => {
    apiSpy = jasmine.createSpyObj('JanusApiService', ['getDemoCards', 'validate']);
    apiSpy.getDemoCards.and.returnValue(of([
      { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte démo active', balanceMillimes: 10000 },
      { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte démo révoquée', balanceMillimes: 5000 }
    ]));

    await TestBed.configureTestingModule({
      imports: [SimulatorComponent],
      providers: [
        { provide: JanusApiService, useValue: apiSpy },
        SessionService,
        ValidationSessionService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SimulatorComponent);
    component = fixture.componentInstance;
    sessionService = TestBed.inject(SessionService);
    validationSession = TestBed.inject(ValidationSessionService);
    fixture.detectChanges();
  });

  it('devrait créer le composant simulateur et charger les cartes de démo', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getDemoCards).toHaveBeenCalled();
    expect(component.demoCards().length).toBe(2);
    expect(component.selectedUid).toBe('04A1B2C3D4E5F6');
  });

  it('devrait exécuter une validation standard et afficher la décision ACCEPTED', () => {
    const mockResult: ValidationResult = {
      eventId: 'test-event-uuid',
      terminalId: 'TERMINAL-DEMO-01',
      cardUid: '04A1B2C3D4E5F6',
      decision: 'ACCEPTED',
      reason: 'ACTIVE_CARD',
      balanceMillimes: 8500,
      amountDebited: 1500,
      duplicate: false,
      receivedAt: '2026-10-16T10:00:00Z'
    };
    apiSpy.validate.and.returnValue(of(mockResult));

    component.simulateValidation();

    expect(apiSpy.validate).toHaveBeenCalled();
    expect(component.currentResult()).toEqual(mockResult);
    expect(component.currentResult()?.decision).toBe('ACCEPTED');
    expect(validationSession.count()).toBe(1);
  });

  it('devrait refuser la simulation de rejeu si aucune transaction préalable n\'a eu lieu', () => {
    component.simulateDuplicate();
    // Aucune requête ne doit être envoyée
    expect(apiSpy.validate).not.toHaveBeenCalled();
  });

  it('devrait permettre de tester le rejeu (idempotence) en réutilisant exactement le même EventId', () => {
    const initialResult: ValidationResult = {
      eventId: 'unique-event-id-123',
      terminalId: 'TERMINAL-DEMO-01',
      cardUid: '04A1B2C3D4E5F6',
      decision: 'ACCEPTED',
      reason: 'ACTIVE_CARD',
      balanceMillimes: 8500,
      amountDebited: 1500,
      duplicate: false,
      receivedAt: '2026-10-16T10:00:00Z'
    };
    apiSpy.validate.and.returnValue(of(initialResult));

    // 1ère validation
    component.simulateValidation();
    expect(apiSpy.validate).toHaveBeenCalledTimes(1);

    // Simulation de rejeu
    const duplicateResult: ValidationResult = {
      ...initialResult,
      duplicate: true,
      amountDebited: 0
    };
    apiSpy.validate.and.returnValue(of(duplicateResult));

    component.simulateDuplicate();
    expect(apiSpy.validate).toHaveBeenCalledTimes(2);
    expect(component.currentResult()?.duplicate).toBeTrue();
  });
});
