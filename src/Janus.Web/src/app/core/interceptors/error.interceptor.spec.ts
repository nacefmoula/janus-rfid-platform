import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import {
  errorInterceptor,
  SKIP_ERROR_TOAST_CONTEXT,
  SKIP_GLOBAL_ERROR_TOAST_HEADER
} from './error.interceptor';
import { NotificationService } from '../services/notification.service';

describe('errorInterceptor', () => {
  let httpClient: HttpClient;
  let httpMock: HttpTestingController;
  let notification: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        NotificationService,
        provideHttpClient(withInterceptors([errorInterceptor])),
        provideHttpClientTesting()
      ]
    });

    httpClient = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    notification = TestBed.inject(NotificationService);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('affiche un toast explicite pour 401 Non autorisé', () => {
    httpClient.get('/api/dashboard/summary').subscribe({ error: () => {} });
    const req = httpMock.expectOne('/api/dashboard/summary');
    req.flush({ error: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(notification.toasts().length).toBe(1);
    expect(notification.toasts()[0].title).toContain('401');
  });

  it('affiche un toast explicite pour 403 Accès refusé', () => {
    httpClient.get('/api/demo/cards').subscribe({ error: () => {} });
    const req = httpMock.expectOne('/api/demo/cards');
    req.flush({ error: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });

    expect(notification.toasts().length).toBe(1);
    expect(notification.toasts()[0].title).toContain('403');
  });

  it('affiche un toast explicite pour 404 Ressource introuvable', () => {
    httpClient.get('/api/cards/UNKNOWN/audit').subscribe({ error: () => {} });
    const req = httpMock.expectOne('/api/cards/UNKNOWN/audit');
    req.flush({ error: 'Carte introuvable' }, { status: 404, statusText: 'Not Found' });

    expect(notification.toasts().length).toBe(1);
    expect(notification.toasts()[0].title).toContain('404');
    expect(notification.toasts()[0].message).toContain('Carte introuvable');
  });

  it('affiche un toast explicite pour 409 Conflit d\'idempotence', () => {
    httpClient.post('/api/validations', {}).subscribe({ error: () => {} });
    const req = httpMock.expectOne('/api/validations');
    req.flush({ error: 'Conflit RequestId' }, { status: 409, statusText: 'Conflict' });

    expect(notification.toasts().length).toBe(1);
    expect(notification.toasts()[0].title).toContain('409');
  });

  it('affiche un toast explicite pour 429 Quota de requêtes dépassé', () => {
    httpClient.post('/api/validations', {}).subscribe({ error: () => {} });
    const req = httpMock.expectOne('/api/validations');
    req.flush({ error: 'Too Many Requests' }, { status: 429, statusText: 'Too Many Requests' });

    expect(notification.toasts().length).toBe(1);
    expect(notification.toasts()[0].title).toContain('429');
  });

  it('affiche le code HTTP réel pour les erreurs 5xx (500 et 503)', () => {
    httpClient.get('/api/dashboard/summary').subscribe({ error: () => {} });
    const req503 = httpMock.expectOne('/api/dashboard/summary');
    req503.flush({}, { status: 503, statusText: 'Service Unavailable' });

    expect(notification.toasts().length).toBe(1);
    expect(notification.toasts()[0].title).toContain('503');
    expect(notification.toasts()[0].message).toContain('503');
  });

  it('affiche un message réseau dédié lorsque status === 0', () => {
    httpClient.get('/api/dashboard/summary').subscribe({ error: () => {} });
    const req = httpMock.expectOne('/api/dashboard/summary');
    req.error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });

    expect(notification.toasts().length).toBe(1);
    expect(notification.toasts()[0].title).toContain('Erreur réseau');
    expect(notification.toasts()[0].message).toContain('Impossible de joindre le serveur API Janus');
  });

  it('ne déclenche jamais de toast global sur /health', () => {
    httpClient.get('/health').subscribe({ error: () => {} });
    const req = httpMock.expectOne('/health');
    req.flush({}, { status: 503, statusText: 'Service Unavailable' });

    expect(notification.toasts().length).toBe(0);
  });

  it('respecte X-Skip-Global-Error-Toast / SKIP_ERROR_TOAST_CONTEXT et supprime l\'en-tête interne avant envoi réseau', () => {
    httpClient.post('/api/validations', {}, {
      headers: { [SKIP_GLOBAL_ERROR_TOAST_HEADER]: 'true' },
      context: new HttpContext().set(SKIP_ERROR_TOAST_CONTEXT, true)
    }).subscribe({ error: () => {} });

    const req = httpMock.expectOne('/api/validations');
    expect(req.request.headers.has(SKIP_GLOBAL_ERROR_TOAST_HEADER)).toBeFalse();
    req.flush({ error: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(notification.toasts().length).toBe(0);
  });
});
