import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { SessionService } from '../services/session.service';

/**
 * Intercepteur d'authentification Bearer contextuel.
 * Injecte le token approprié selon la ressource ciblée :
 * - /api/validations -> Jeton Borne / Terminal
 * - /api/cards/{uid}/audit, PUT /api/cards/{uid} et /api/demo/cards -> Jeton Administrateur
 * - GET /api/cards/{uid}, /api/simulator/cards et /api/dashboard/summary -> Jeton effectif (Terminal ou Admin)
 * Aucun secret de scraping (/metrics) n'est détenu ni transmis par le navigateur.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SessionService);

  // Endpoint public : pas d'en-tête nécessaire
  if (req.url.endsWith('/health')) {
    return next(req);
  }

  let token: string | null = null;

  if (req.url.includes('/api/validations')) {
    // Les validations de transport exigent un jeton de borne RFID
    token = session.terminalToken() ?? (session.activeRole() === 'TERMINAL' ? session.getEffectiveToken(false) : null);
  } else if (req.url.includes('/audit') || (req.method === 'PUT' && req.url.includes('/api/cards/')) || req.url.includes('/api/demo/cards')) {
    // Les opérations strictement administratives exigent le jeton administrateur
    token = session.adminToken() ?? (session.activeRole() === 'ADMIN' ? session.getEffectiveToken(true) : null);
  } else if (req.url.includes('/api/cards/') || req.url.includes('/api/simulator/cards') || req.url.includes('/api/dashboard/summary')) {
    // La consultation détaillée, le simulateur et le résumé dashboard acceptent un jeton borne ou admin
    token = session.getEffectiveToken() ?? session.terminalToken() ?? session.adminToken();
  }

  if (token) {
    const cloned = req.clone({
      setHeaders: {
        Authorization: `Bearer ${token}`
      }
    });
    return next(cloned);
  }

  return next(req);
};
