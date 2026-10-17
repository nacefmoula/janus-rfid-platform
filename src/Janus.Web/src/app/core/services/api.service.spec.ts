import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { JanusApiService } from './api.service';
import { ValidationRequest } from '../models/validation.model';

describe('JanusApiService', () => {
  let service: JanusApiService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        JanusApiService,
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });
    service = TestBed.inject(JanusApiService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('devrait appeler GET /health pour vérifier la disponibilité de l\'API', () => {
    service.getHealth().subscribe(res => {
      expect(res.status).toBe('healthy');
      expect(res.service).toBe('janus-api');
    });

    const req = httpMock.expectOne('/health');
    expect(req.request.method).toBe('GET');
    req.flush({ status: 'healthy', service: 'janus-api' });
  });

  it('devrait appeler GET /api/demo/cards pour lister les cartes de démonstration', () => {
    service.getDemoCards().subscribe(cards => {
      expect(cards.length).toBe(2);
      expect(cards[0].uid).toBe('04A1B2C3D4E5F6');
      expect(cards[0].balanceMillimes).toBe(10000);
    });

    const req = httpMock.expectOne('/api/demo/cards');
    expect(req.request.method).toBe('GET');
    req.flush([
      { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte démo active', balanceMillimes: 10000 },
      { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte démo révoquée', balanceMillimes: 5000 }
    ]);
  });

  it('devrait soumettre une validation transport via POST /api/validations', () => {
    const payload: ValidationRequest = {
      eventId: '11111111-2222-3333-4444-555555555555',
      cardUid: '04A1B2C3D4E5F6',
      occurredAt: '2026-10-16T10:00:00Z',
      eventType: 'TRANSPORT_VALIDATION'
    };

    service.validate(payload).subscribe(result => {
      expect(result.decision).toBe('ACCEPTED');
      expect(result.amountDebited).toBe(1500);
      expect(result.balanceMillimes).toBe(8500);
      expect(result.duplicate).toBeFalse();
    });

    const req = httpMock.expectOne('/api/validations');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(payload);
    req.flush({
      eventId: payload.eventId,
      terminalId: 'TERMINAL-DEMO-01',
      cardUid: payload.cardUid,
      decision: 'ACCEPTED',
      reason: 'ACTIVE_CARD',
      balanceMillimes: 8500,
      amountDebited: 1500,
      duplicate: false,
      receivedAt: '2026-10-16T10:00:01Z'
    });
  });

  it('devrait appeler GET /api/simulator/cards pour alimenter le simulateur sans privilèges admin ni exposition du solde', () => {
    service.getSimulatorCards().subscribe(cards => {
      expect(cards.length).toBe(2);
      expect(cards[0].uid).toBe('04A1B2C3D4E5F6');
      expect(cards[0].balanceMillimes).toBeUndefined();
    });

    const req = httpMock.expectOne('/api/simulator/cards');
    expect(req.request.method).toBe('GET');
    req.flush([
      { uid: '04A1B2C3D4E5F6', status: 'Active', label: 'Carte démo active' },
      { uid: '04DEAD00000001', status: 'Revoked', label: 'Carte démo révoquée' }
    ]);
  });

  it('devrait récupérer la synthèse métier via GET /api/dashboard/summary sans appeler /metrics', () => {
    const mockSummary = {
      totalValidations: 15,
      acceptedValidations: 12,
      rejectedValidations: 3,
      conflictValidations: 1,
      totalDebitedMillimes: 18000,
      activeCardsCount: 2,
      revokedCardsCount: 1,
      totalCardsCount: 3,
      totalCirculatingBalanceMillimes: 25000,
      scrapedAt: '2026-10-16T10:00:00Z'
    };

    service.getMetricsSummary().subscribe(summary => {
      expect(summary.totalValidations).toBe(15);
      expect(summary.acceptedValidations).toBe(12);
      expect(summary.rejectedValidations).toBe(3);
      expect(summary.conflictValidations).toBe(1);
      expect(summary.totalDebitedMillimes).toBe(18000);
      expect(summary.activeCardsCount).toBe(2);
      expect(summary.scrapedAt instanceof Date).toBeTrue();
    });

    const req = httpMock.expectOne('/api/dashboard/summary');
    expect(req.request.method).toBe('GET');
    req.flush(mockSummary);
  });
});
