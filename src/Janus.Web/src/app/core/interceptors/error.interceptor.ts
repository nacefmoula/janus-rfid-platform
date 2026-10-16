import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { NotificationService } from '../services/notification.service';

/**
 * Intercepteur de gestion centralisée des erreurs HTTP.
 * Traduit les statuts HTTP de Janus API en messages compréhensibles et notifie l'utilisateur.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const notification = inject(NotificationService);

  return next(req).pipe(
    catchError((error: HttpErrorResponse) => {
      let title = 'Erreur réseau';
      let message = 'Impossible de joindre le serveur API Janus.';

      if (error.status === 401) {
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
      } else if (error.status === 400) {
        title = 'Requête invalide (400)';
        message = error.error?.message || error.error?.error || 'Paramètres de requête incorrects.';
      } else if (error.status >= 500) {
        title = 'Erreur serveur interne (500)';
        message = error.error?.message || 'Le serveur a rencontré un incident.';
      }

      // N'affiche pas de toast d'erreur automatique pour les requêtes de test intentionnel du simulateur si le composant les intercepte
      if (!req.headers.has('X-Skip-Global-Error-Toast')) {
        notification.error(title, message);
      }

      return throwError(() => error);
    })
  );
};
