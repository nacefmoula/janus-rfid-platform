import { HttpContextToken, HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { NotificationService } from '../services/notification.service';

export const SKIP_ERROR_TOAST_CONTEXT = new HttpContextToken<boolean>(() => false);
export const SKIP_GLOBAL_ERROR_TOAST_HEADER = 'X-Skip-Global-Error-Toast';

function isHealthCheckUrl(url: string): boolean {
  const path = url.split('?')[0];
  return path === '/health' || path.endsWith('/health');
}

/**
 * Intercepteur de gestion centralisée des erreurs HTTP.
 * Traduit les statuts HTTP de Janus API en messages compréhensibles et notifie l'utilisateur,
 * sans polluer les sondes périodiques (/health) ni dupliquer les erreurs gérées localement.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const notification = inject(NotificationService);

  const hasSkipHeader = req.headers.has(SKIP_GLOBAL_ERROR_TOAST_HEADER);
  const skipToast =
    hasSkipHeader ||
    req.context.get(SKIP_ERROR_TOAST_CONTEXT) ||
    isHealthCheckUrl(req.url);

  const outgoingReq = hasSkipHeader
    ? req.clone({ headers: req.headers.delete(SKIP_GLOBAL_ERROR_TOAST_HEADER) })
    : req;

  return next(outgoingReq).pipe(
    catchError((error: HttpErrorResponse) => {
      let title = 'Erreur réseau';
      let message = 'Impossible de joindre le serveur API Janus.';

      if (error.status === 0) {
        title = 'Erreur réseau';
        message = 'Impossible de joindre le serveur API Janus.';
      } else if (error.status === 400) {
        title = 'Requête invalide (400)';
        message = error.error?.message || error.error?.error || 'Paramètres de requête incorrects.';
      } else if (error.status === 401) {
        title = 'Authentification requise (401)';
        message = error.error?.message || 'Identifiant de borne ou administrateur manquant ou invalide.';
      } else if (error.status === 403) {
        title = 'Accès interdit (403)';
        message = error.error?.message || 'Les droits associés à ce jeton ne permettent pas cette opération.';
      } else if (error.status === 404) {
        title = 'Ressource introuvable (404)';
        message = error.error?.message || error.error?.error || 'La ressource demandée n\'existe pas.';
      } else if (error.status === 409) {
        title = 'Conflit d\'idempotence (409)';
        message = error.error?.message || 'Cet identifiant d\'événement a déjà été utilisé avec un payload différent.';
      } else if (error.status === 429) {
        title = 'Quota de requêtes dépassé (429)';
        message = error.error?.message || 'Limitation de débit atteinte. Veuillez patienter avant de réessayer.';
      } else if (error.status >= 500) {
        title = `Erreur serveur (${error.status})`;
        message = error.error?.message || `Le serveur a rencontré un incident interne (HTTP ${error.status}).`;
      } else {
        title = `Erreur HTTP (${error.status})`;
        message = error.error?.message || error.message || 'Une erreur est survenue lors du traitement de la requête.';
      }

      if (!skipToast) {
        notification.error(title, message);
      }

      return throwError(() => error);
    })
  );
};
