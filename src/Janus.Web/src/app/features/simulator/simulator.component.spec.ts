import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of, Subject, throwError } from 'rxjs';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { SimulatorComponent } from './simulator.component';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';
import { ValidationResult } from '../../core/models/validation.model';
import { SimulatorCard } from '../../core/models/card.model';

describe('SimulatorComponent', () => {
  let component: SimulatorComponent;
  let fixture: ComponentFixture<SimulatorComponent>;
  let apiSpy: jasmine.SpyObj<JanusApiService>;
  let sessionService: SessionService;
  let validationSession: ValidationSessionService;

  beforeEach(async () => {
    apiSpy = jasmine.createSpyObj('JanusApiService', ['getSimulatorCards', 'getDemoCards', 'validate']);
    apiSpy.getSimulatorCards.and.returnValue(of([
      { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte démo active' },
      { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte démo révoquée' }
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

  it('devrait créer le composant simulateur et charger les cartes via getSimulatorCards sans appeler getDemoCards ni exposer le solde initial', () => {
    expect(component).toBeTruthy();
    expect(apiSpy.getSimulatorCards).toHaveBeenCalled();
    expect(apiSpy.getDemoCards).not.toHaveBeenCalled();
    expect(component.demoCards().length).toBe(2);
    expect(component.selectedUid).toBe('04A1B2C3D4E5F6');
    expect(component.balanceBefore()).toBeNull();
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

  it('devrait afficher la décision REJECTED et le motif PASSBACK_VIOLATION avec montant débité nul', () => {
    const passbackResult: ValidationResult = {
      eventId: 'test-passback-uuid',
      terminalId: 'TERMINAL-DEMO-01',
      cardUid: '04A1B2C3D4E5F6',
      decision: 'REJECTED',
      reason: 'PASSBACK_VIOLATION',
      balanceMillimes: 8500,
      amountDebited: 0,
      duplicate: false,
      receivedAt: '2026-10-16T10:00:05Z'
    };
    apiSpy.validate.and.returnValue(of(passbackResult));

    component.simulateValidation();

    expect(apiSpy.validate).toHaveBeenCalled();
    expect(component.currentResult()).toEqual(passbackResult);
    expect(component.currentResult()?.decision).toBe('REJECTED');
    expect(component.currentResult()?.reason).toBe('PASSBACK_VIOLATION');
    expect(component.currentResult()?.amountDebited).toBe(0);

    fixture.detectChanges();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('.screen-reason')?.textContent).toContain('Anti-passback : Titre déjà validé récemment');
  });

  it('5. simulateUnauthorized() utilise un bypass par requête (skipAuth + skipErrorToast) et ne modifie jamais SessionService', () => {
    sessionService.configureSession({
      terminalToken: 'borne-token-actif',
      adminToken: 'admin-token-actif',
      role: 'TERMINAL'
    });
    const versionBefore = sessionService.sessionVersion();

    apiSpy.validate.and.returnValue(throwError(() => ({
      status: 401,
      error: { error: 'Unauthorized' }
    })));

    component.simulateUnauthorized();

    expect(apiSpy.validate).toHaveBeenCalledWith(
      jasmine.objectContaining({ cardUid: '04A1B2C3D4E5F6' }),
      { skipAuth: true, skipErrorToast: true }
    );
    expect(sessionService.activeRole()).toBe('TERMINAL');
    expect(sessionService.terminalToken()).toBe('borne-token-actif');
    expect(sessionService.adminToken()).toBe('admin-token-actif');
    expect(sessionService.sessionVersion()).toBe(versionBefore);
    expect(component.currentError()?.status).toBe(401);
  });

  it('6 & 8. clearTokens() purge l\'état du simulateur et ignore toute réponse tardive issue d\'une ancienne session', () => {
    const delayedCards$ = new Subject<SimulatorCard[]>();
    apiSpy.getSimulatorCards.and.returnValue(delayedCards$.asObservable());

    component.loadDemoCards();
    expect(component.loadingCards()).toBeTrue();

    sessionService.clearTokens();
    expect(component.demoCards()).toEqual([]);
    expect(component.loadingCards()).toBeFalse();

    // Une réponse tardive de l'ancienne session ne doit pas réécraser demoCards
    delayedCards$.next([{ uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte tardive' }]);
    expect(component.demoCards()).toEqual([]);
  });
});
