import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { SessionService } from '../services/session.service';

describe('authInterceptor', () => {
  let httpClient: HttpClient;
  let httpMock: HttpTestingController;
  let session: SessionService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        SessionService,
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting()
      ]
    });
    httpClient = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    session = TestBed.inject(SessionService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('ne devrait PAS injecter d\'Authorization sur les routes publiques (/health, /api/demo/cards)', () => {
    session.setTerminalToken('token-borne-actif');

    httpClient.get('/health').subscribe();
    const reqHealth = httpMock.expectOne('/health');
    expect(reqHealth.request.headers.has('Authorization')).toBeFalse();
    reqHealth.flush({});

    httpClient.get('/api/demo/cards').subscribe();
    const reqDemo = httpMock.expectOne('/api/demo/cards');
    expect(reqDemo.request.headers.has('Authorization')).toBeFalse();
    reqDemo.flush([]);
  });

  it('devrait injecter le Bearer token de borne sur /api/validations si présent en mémoire', () => {
    session.setTerminalToken('mon-token-borne-123');

    httpClient.post('/api/validations', {}).subscribe();
    const req = httpMock.expectOne('/api/validations');
    expect(req.request.headers.get('Authorization')).toBe('Bearer mon-token-borne-123');
    req.flush({});
  });

  it('devrait injecter le Bearer token admin sur /api/cards/{uid}/audit', () => {
    session.setAdminToken('mon-token-admin-456');

    httpClient.get('/api/cards/04A1B2C3/audit').subscribe();
    const req = httpMock.expectOne('/api/cards/04A1B2C3/audit');
    expect(req.request.headers.get('Authorization')).toBe('Bearer mon-token-admin-456');
    req.flush({});
  });

  it('devrait laisser la requête sans en-tête d\'autorisation si aucun jeton n\'est présent', () => {
    session.clearTokens();

    httpClient.post('/api/validations', {}).subscribe();
    const req = httpMock.expectOne('/api/validations');
    expect(req.request.headers.has('Authorization')).toBeFalse();
    req.flush({});
  });
});
