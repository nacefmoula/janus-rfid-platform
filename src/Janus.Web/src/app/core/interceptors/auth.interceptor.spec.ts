import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor, SKIP_AUTH_CONTEXT, SKIP_AUTH_HEADER } from './auth.interceptor';
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

  it('ne devrait PAS injecter d\'Authorization sur la route publique (/health)', () => {
    session.setTerminalToken('token-borne-actif');

    httpClient.get('/health').subscribe();
    const reqHealth = httpMock.expectOne('/health');
    expect(reqHealth.request.headers.has('Authorization')).toBeFalse();
    reqHealth.flush({});
  });

  it('devrait injecter le Bearer token admin sur /api/demo/cards si présent en rôle ADMIN', () => {
    session.setAdminToken('mon-token-admin-demo');

    httpClient.get('/api/demo/cards').subscribe();
    const reqDemo = httpMock.expectOne('/api/demo/cards');
    expect(reqDemo.request.headers.get('Authorization')).toBe('Bearer mon-token-admin-demo');
    reqDemo.flush([]);
  });

  it('devrait injecter le Bearer token de borne sur /api/validations si présent en mémoire', () => {
    session.setTerminalToken('mon-token-borne-123');

    httpClient.post('/api/validations', {}).subscribe();
    const req = httpMock.expectOne('/api/validations');
    expect(req.request.headers.get('Authorization')).toBe('Bearer mon-token-borne-123');
    req.flush({});
  });

  it('devrait injecter le Bearer token admin sur /api/cards/{uid}/audit en rôle ADMIN', () => {
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

  it('devrait injecter le Bearer token de borne sur /api/dashboard/summary et /api/simulator/cards sans exiger de token admin', () => {
    session.clearTokens();
    session.setTerminalToken('mon-token-borne-operateur');

    httpClient.get('/api/dashboard/summary').subscribe();
    const reqDash = httpMock.expectOne('/api/dashboard/summary');
    expect(reqDash.request.headers.get('Authorization')).toBe('Bearer mon-token-borne-operateur');
    reqDash.flush({});

    httpClient.get('/api/simulator/cards').subscribe();
    const reqSim = httpMock.expectOne('/api/simulator/cards');
    expect(reqSim.request.headers.get('Authorization')).toBe('Bearer mon-token-borne-operateur');
    reqSim.flush([]);
  });

  it('ne devrait jamais injecter de jeton sur /metrics depuis le navigateur', () => {
    session.setAdminToken('mon-token-admin-456');
    session.setTerminalToken('mon-token-borne-123');

    httpClient.get('/metrics').subscribe();
    const reqMetrics = httpMock.expectOne('/metrics');
    expect(reqMetrics.request.headers.has('Authorization')).toBeFalse();
    reqMetrics.flush('');
  });

  it('1. ANONYMOUS n\'envoie aucun jeton sur toutes les routes API, même si des jetons Terminal et Admin sont présents en mémoire', () => {
    session.configureSession({
      terminalToken: 'token-borne-secret',
      adminToken: 'token-admin-secret',
      role: 'ANONYMOUS'
    });

    httpClient.get('/api/dashboard/summary').subscribe();
    const reqDash = httpMock.expectOne('/api/dashboard/summary');
    expect(reqDash.request.headers.has('Authorization')).toBeFalse();
    reqDash.flush({});

    httpClient.get('/api/demo/cards').subscribe();
    const reqAdmin = httpMock.expectOne('/api/demo/cards');
    expect(reqAdmin.request.headers.has('Authorization')).toBeFalse();
    reqAdmin.flush({});

    httpClient.post('/api/validations', {}).subscribe();
    const reqVal = httpMock.expectOne('/api/validations');
    expect(reqVal.request.headers.has('Authorization')).toBeFalse();
    reqVal.flush({});
  });

  it('2. TERMINAL envoie uniquement le jeton Terminal, même si un jeton Admin est présent en mémoire (et envoie le jeton Terminal sur les endpoints Admin pour provoquer un vrai 403 RBAC)', () => {
    session.configureSession({
      terminalToken: 'token-borne-uniquement',
      adminToken: 'token-admin-en-memoire',
      role: 'TERMINAL'
    });

    httpClient.get('/api/dashboard/summary').subscribe();
    const reqDash = httpMock.expectOne('/api/dashboard/summary');
    expect(reqDash.request.headers.get('Authorization')).toBe('Bearer token-borne-uniquement');
    reqDash.flush({});

    httpClient.get('/api/demo/cards').subscribe();
    const reqCards = httpMock.expectOne('/api/demo/cards');
    expect(reqCards.request.headers.get('Authorization')).toBe('Bearer token-borne-uniquement');
    reqCards.flush({});

    httpClient.get('/api/cards/04A1B2C3/audit').subscribe();
    const reqAudit = httpMock.expectOne('/api/cards/04A1B2C3/audit');
    expect(reqAudit.request.headers.get('Authorization')).toBe('Bearer token-borne-uniquement');
    reqAudit.flush({});
  });

  it('3. ADMIN envoie uniquement le jeton Admin, même si un jeton Terminal est présent en mémoire', () => {
    session.configureSession({
      terminalToken: 'token-borne-en-memoire',
      adminToken: 'token-admin-uniquement',
      role: 'ADMIN'
    });

    httpClient.post('/api/validations', {}).subscribe();
    const reqVal = httpMock.expectOne('/api/validations');
    expect(reqVal.request.headers.get('Authorization')).toBe('Bearer token-admin-uniquement');
    reqVal.flush({});

    httpClient.get('/api/demo/cards').subscribe();
    const reqCards = httpMock.expectOne('/api/demo/cards');
    expect(reqCards.request.headers.get('Authorization')).toBe('Bearer token-admin-uniquement');
    reqCards.flush({});
  });

  it('5. supporte un bypass par requête (SKIP_AUTH_CONTEXT et X-Skip-Auth) sans modifier SessionService ni exposer X-Skip-Auth sur le réseau', () => {
    session.configureSession({
      terminalToken: 'token-borne-actif',
      adminToken: 'token-admin-actif',
      role: 'ADMIN'
    });

    httpClient.post('/api/validations', {}, {
      context: new HttpContext().set(SKIP_AUTH_CONTEXT, true),
      headers: { [SKIP_AUTH_HEADER]: 'true' }
    }).subscribe();

    const reqBypass = httpMock.expectOne('/api/validations');
    expect(reqBypass.request.headers.has('Authorization')).toBeFalse();
    expect(reqBypass.request.headers.has(SKIP_AUTH_HEADER)).toBeFalse();
    reqBypass.flush({});

    // La session globale reste intacte et la requête suivante est bien authentifiée
    expect(session.activeRole()).toBe('ADMIN');
    expect(session.adminToken()).toBe('token-admin-actif');

    httpClient.post('/api/validations', {}).subscribe();
    const reqNormal = httpMock.expectOne('/api/validations');
    expect(reqNormal.request.headers.get('Authorization')).toBe('Bearer token-admin-actif');
    reqNormal.flush({});
  });
});
