import { TestBed } from '@angular/core/testing';
import { SessionService } from './session.service';

describe('SessionService', () => {
  let service: SessionService;

  beforeEach(() => {
    TestBed.configureTestingModule({});
    service = TestBed.inject(SessionService);
  });

  it('devrait être instancié avec un état anonyme initial', () => {
    expect(service).toBeTruthy();
    expect(service.activeRole()).toBe('ANONYMOUS');
    expect(service.terminalToken()).toBeNull();
    expect(service.adminToken()).toBeNull();
    expect(service.hasTerminalAuth()).toBeFalse();
    expect(service.hasAdminAuth()).toBeFalse();
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

  it('devrait vider tous les jetons de la mémoire sur demande explicite', () => {
    service.setTerminalToken('token-1');
    service.setAdminToken('token-2');

    service.clearTokens();

    expect(service.terminalToken()).toBeNull();
    expect(service.adminToken()).toBeNull();
    expect(service.activeRole()).toBe('ANONYMOUS');
    expect(service.hasTerminalAuth()).toBeFalse();
    expect(service.hasAdminAuth()).toBeFalse();
  });

  it('devrait résoudre le jeton effectif selon le contexte de privilège requis', () => {
    service.setTerminalToken('terminal-token');
    service.setAdminToken('admin-token');

    // Si rôle = ADMIN, getEffectiveToken() renvoie adminToken
    expect(service.getEffectiveToken()).toBe('admin-token');

    // Si on demande explicitement preferAdmin
    expect(service.getEffectiveToken(true)).toBe('admin-token');

    // Si le rôle repasse à TERMINAL
    service.setActiveRole('TERMINAL');
    expect(service.getEffectiveToken(false)).toBe('terminal-token');
  });
});
