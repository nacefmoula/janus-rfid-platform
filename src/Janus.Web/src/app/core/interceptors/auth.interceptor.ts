import { HttpContextToken, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { SessionService } from '../services/session.service';

/**
 * Jetons de contexte / en-tête interne permettant d'omettre volontairement l'authentification
 * sur une requête isolée (ex: scénario de test 401 du simulateur RFID) sans modifier SessionService.
 */
export const SKIP_AUTH_CONTEXT = new HttpContextToken<boolean>(() => false);
export const SKIP_AUTH_HEADER = 'X-Skip-Auth';

/**
 * Intercepteur d'authentification Bearer gouverné strictement par le rôle actif :
 * - ANONYMOUS : aucun en-tête Authorization transmis.
 * - TERMINAL  : uniquement le jeton Terminal (permettant notamment d'obtenir un vrai 403 Forbidden backend sur les routes AdminOnly).
 * - ADMIN     : uniquement le jeton Administrateur (n'envoie jamais accidentellement le jeton Terminal).
 * - /health et /metrics : aucun jeton navigateur injecté.
 */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const session = inject(SessionService);

  // Bypass explicite strictement borné à la requête courante (scénario de test 401 du simulateur).
  // L'en-tête interne éventuel est supprimé avant l'envoi réseau.
  if (req.context.get(SKIP_AUTH_CONTEXT) || req.headers.has(SKIP_AUTH_HEADER)) {
    const cleanHeaders = req.headers.delete(SKIP_AUTH_HEADER).delete('Authorization');
    return next(req.clone({ headers: cleanHeaders }));
  }

  // Endpoints publics ou de scraping interne : pas d'en-tête Authorization depuis le navigateur
  if (req.url.endsWith('/health') || req.url.includes('/metrics') || !req.url.includes('/api/')) {
    return next(req);
  }

  const token = session.getEffectiveToken();

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
