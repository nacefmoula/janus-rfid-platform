import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { SessionService } from './session.service';
import { ValidationSessionService } from './validation-session.service';
import { NotificationService } from './notification.service';
import { HeaderComponent } from '../../shared/components/header/header.component';

describe('SessionService & HeaderComponent Session Management', () => {
  let service: SessionService;
  let validationSession: ValidationSessionService;
  let notification: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HeaderComponent],
      providers: [
        SessionService,
        ValidationSessionService,
        NotificationService,
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });
    service = TestBed.inject(SessionService);
    validationSession = TestBed.inject(ValidationSessionService);
    notification = TestBed.inject(NotificationService);
  });

  it('devrait être instancié avec un état anonyme initial', () => {
    expect(service).toBeTruthy();
    expect(service.activeRole()).toBe('ANONYMOUS');
    expect(service.terminalToken()).toBeNull();
    expect(service.adminToken()).toBeNull();
    expect(service.hasTerminalAuth()).toBeFalse();
    expect(service.hasAdminAuth()).toBeFalse();
    expect(service.getEffectiveToken()).toBeNull();
  });

  it('devrait enregistrer un jeton terminal en mémoire vive sans toucher au localStorage', () => {
    service.setTerminalToken('test-terminal-secret-token');

    expect(service.terminalToken()).toBe('test-terminal-secret-token');
    expect(service.hasTerminalAuth()).toBeTrue();
    expect(service.activeRole()).toBe('TERMINAL');

    // Vérifie que localStorage ne contient aucun token
    expect(localStorage.getItem('terminal_token')).toBeNull();
    expect(localStorage.getItem('token')).toBeNull();
  });

  it('devrait enregistrer un jeton administrateur et promouvoir le rôle actif', () => {
    service.setAdminToken('test-admin-secret-token');

    expect(service.adminToken()).toBe('test-admin-secret-token');
    expect(service.hasAdminAuth()).toBeTrue();
    expect(service.activeRole()).toBe('ADMIN');
    expect(service.getEffectiveToken(true)).toBe('test-admin-secret-token');
  });

  it('devrait vider tous les jetons ainsi que le journal de session de validation sur clearTokens()', () => {
    service.setTerminalToken('token-1');
    service.setAdminToken('token-2');
    validationSession.addEntry({
      id: 'evt-1',
      timestamp: new Date(),
      request: { eventId: 'evt-1', cardUid: '04A1B2C3D4E5F6', eventType: 'TRANSPORT_VALIDATION' },
      durationMs: 12,
      scenario: 'STANDARD'
    });
    expect(validationSession.count()).toBe(1);

    service.clearTokens();

    expect(service.terminalToken()).toBeNull();
    expect(service.adminToken()).toBeNull();
    expect(service.activeRole()).toBe('ANONYMOUS');
    expect(service.hasTerminalAuth()).toBeFalse();
    expect(service.hasAdminAuth()).toBeFalse();
    expect(validationSession.count()).toBe(0);
  });

  it('devrait isoler strictement le jeton selon le rôle actif (ANONYMOUS, TERMINAL, ADMIN)', () => {
    service.setTerminalToken('terminal-token');
    service.setAdminToken('admin-token');

    // Si rôle = ADMIN, getEffectiveToken() renvoie strictement admin-token
    expect(service.activeRole()).toBe('ADMIN');
    expect(service.getEffectiveToken()).toBe('admin-token');
    expect(service.getEffectiveToken(true)).toBe('admin-token');

    // Si le rôle passe à TERMINAL, getEffectiveToken() renvoie terminal-token et ne fuit jamais admin-token
    service.setActiveRole('TERMINAL');
    expect(service.getEffectiveToken()).toBe('terminal-token');
    expect(service.getEffectiveToken(false)).toBe('terminal-token');
    expect(service.getEffectiveToken(true)).toBeNull();

    // Si le rôle passe à ANONYMOUS, aucun jeton ne doit être retourné même s'ils sont en mémoire
    service.setActiveRole('ANONYMOUS');
    expect(service.getEffectiveToken()).toBeNull();
    expect(service.getEffectiveToken(false)).toBeNull();
    expect(service.getEffectiveToken(true)).toBeNull();
  });

  it('HeaderComponent.applySession devrait conserver le rôle choisi (ex: TERMINAL même si adminToken est aussi fourni)', () => {
    const fixture = TestBed.createComponent(HeaderComponent);
    const header = fixture.componentInstance;

    header.tempTerminalToken = 'tok-borne-01';
    header.tempAdminToken = 'tok-admin-01';
    header.selectRole('TERMINAL');
    header.applySession();

    expect(service.activeRole()).toBe('TERMINAL');
    expect(service.terminalToken()).toBe('tok-borne-01');
    expect(service.adminToken()).toBe('tok-admin-01');
    expect(service.getEffectiveToken()).toBe('tok-borne-01');
  });

  it('HeaderComponent.applySession ne devrait pas écraser le rôle par ANONYMOUS lors de la première saisie d\'un jeton', () => {
    const fixture = TestBed.createComponent(HeaderComponent);
    const header = fixture.componentInstance;

    // Simule l'ouverture de la modale depuis l'état ANONYMOUS et la saisie d'un jeton borne sans clic sur les boutons de rôle
    header.toggleModal();
    header.onTerminalTokenInput('tok-borne-initial');
    header.applySession();

    expect(service.activeRole()).toBe('TERMINAL');
    expect(service.terminalToken()).toBe('tok-borne-initial');
    expect(header.showModal()).toBeFalse();
  });

  it('HeaderComponent.applySession devrait refuser un rôle incompatible avec les jetons fournis et afficher une erreur explicite sans basculer silencieusement', () => {
    const fixture = TestBed.createComponent(HeaderComponent);
    const header = fixture.componentInstance;

    service.configureSession({
      terminalToken: 'tok-borne-existant',
      adminToken: null,
      role: 'TERMINAL'
    });

    header.toggleModal();
    header.tempTerminalToken = 'tok-borne-existant';
    header.tempAdminToken = '';
    header.selectRole('ADMIN');
    header.applySession();

    // La modale reste ouverte avec un message d'erreur explicite et le rôle en cours n'est pas altéré silencieusement
    expect(header.showModal()).toBeTrue();
    expect(header.sessionError()).toContain('jeton Administrateur');
    expect(service.activeRole()).toBe('TERMINAL');
    expect(notification.toasts().some(t => t.type === 'warning')).toBeTrue();
  });

  it('devrait purger ValidationSessionService lors d\'une transition vers ANONYMOUS via setActiveRole ou configureSession, tout en préservant l\'historique entre TERMINAL et ADMIN', () => {
    const sampleEntry = {
      id: 'evt-transition-1',
      timestamp: new Date(),
      request: { eventId: 'evt-transition-1', cardUid: '04A1B2C3D4E5F6', eventType: 'TRANSPORT_VALIDATION' as const },
      durationMs: 15,
      scenario: 'STANDARD' as const
    };

    service.configureSession({
      terminalToken: 'tok-borne',
      adminToken: 'tok-admin',
      role: 'TERMINAL'
    });
    validationSession.addEntry(sampleEntry);
    expect(validationSession.count()).toBe(1);

    // Transition TERMINAL -> ADMIN : l'historique est conservé
    service.setActiveRole('ADMIN');
    expect(validationSession.count()).toBe(1);

    // Transition ADMIN -> TERMINAL : l'historique est conservé
    service.setActiveRole('TERMINAL');
    expect(validationSession.count()).toBe(1);

    // Transition vers ANONYMOUS via setActiveRole('ANONYMOUS') : purge immédiate
    service.setActiveRole('ANONYMOUS');
    expect(validationSession.count()).toBe(0);

    // Ré-alimente en mode ADMIN puis bascule vers ANONYMOUS via configureSession()
    service.setActiveRole('ADMIN');
    validationSession.addEntry(sampleEntry);
    expect(validationSession.count()).toBe(1);

    service.configureSession({
      terminalToken: 'tok-borne',
      adminToken: 'tok-admin',
      role: 'ANONYMOUS'
    });
    expect(service.activeRole()).toBe('ANONYMOUS');
    expect(validationSession.count()).toBe(0);
  });
});
