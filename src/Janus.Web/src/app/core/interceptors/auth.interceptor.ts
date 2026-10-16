import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { SessionService } from '../services/session.service';

/**
 * Intercepteur d'authentification Bearer contextuel.
 * Injecte le token approprié selon la ressource ciblée :
 * - /api/validations -> Jeton Borne / Terminal
 * - /api/cards/{uid}/audit et PUT /api/cards/{uid} -> Jeton Administrateur
 * - GET /api/cards/{uid} -> Jeton effectif (Terminal ou Admin)
 * Si aucun jeton n'est fourni, la requête est transmise sans en-tête, permettant à l'API
 * de renvoyer un statut 401 Unauthorized ou 403 Forbidden démontrable.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SessionService);

  // Endpoints publics : pas d'en-tête nécessaire
  if (req.url.endsWith('/health') || req.url.endsWith('/api/demo/cards') || req.url.endsWith('/metrics')) {
    return next(req);
  }

  let token: string | null = null;

  if (req.url.includes('/api/validations')) {
    // Les validations de transport exigent un jeton de borne RFID
    token = session.terminalToken() ?? (session.activeRole() === 'TERMINAL' ? session.getEffectiveToken(false) : null);
  } else if (req.url.includes('/audit') || (req.method === 'PUT' && req.url.includes('/api/cards/'))) {
    // Les opérations administratives exigent strictement le jeton admin
    token = session.adminToken() ?? (session.activeRole() === 'ADMIN' ? session.getEffectiveToken(true) : null);
  } else if (req.url.includes('/api/cards/')) {
    // La consultation détaillée accepte un jeton borne ou admin
    token = session.getEffectiveToken();
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
