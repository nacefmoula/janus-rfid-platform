import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpContext, HttpHeaders, HttpParams } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { ApiHealth, PrometheusMetricsSummary } from '../models/health.model';
import { TransitCard, SimulatorCard, CardDetail, CardUpsertRequest, CardAuditResponse } from '../models/card.model';
import { ValidationRequest, ValidationResult } from '../models/validation.model';
import { SKIP_AUTH_CONTEXT, SKIP_AUTH_HEADER } from '../interceptors/auth.interceptor';
import { SKIP_ERROR_TOAST_CONTEXT, SKIP_GLOBAL_ERROR_TOAST_HEADER } from '../interceptors/error.interceptor';

export interface ValidationRequestOptions {
  skipAuth?: boolean;
  skipErrorToast?: boolean;
}

@Injectable({
  providedIn: 'root'
})
export class JanusApiService {
  private readonly http = inject(HttpClient);

  // Utilise le chemin relatif (fonctionne avec le proxy Angular en dev et le reverse-proxy Nginx en prod)
  private readonly basePath = '';

  /**
   * Vérifie la santé de l'API (sélecteur public / healthcheck).
   */
  getHealth(): Observable<ApiHealth> {
    return this.http.get<ApiHealth>(`${this.basePath}/health`);
  }

  /**
   * Récupère la liste complète des cartes enregistrées pour l'administration.
   * Requiert STRICTEMENT les droits Admin : GET /api/demo/cards
   */
  getDemoCards(limit?: number, offset?: number): Observable<TransitCard[]> {
    let params = new HttpParams();
    if (limit !== undefined) params = params.set('limit', limit.toString());
    if (offset !== undefined) params = params.set('offset', offset.toString());
    return this.http.get<TransitCard[]>(`${this.basePath}/api/demo/cards`, {
      params: params.keys().length > 0 ? params : undefined
    });
  }

  /**
   * Récupère la liste minimale des cartes (uid, status, label sans solde financier) pour le simulateur ou la vue lecture borne.
   * Accessible aux rôles Terminal et Admin : GET /api/simulator/cards
   */
  getSimulatorCards(limit?: number, offset?: number): Observable<SimulatorCard[]> {
    let params = new HttpParams();
    if (limit !== undefined) params = params.set('limit', limit.toString());
    if (offset !== undefined) params = params.set('offset', offset.toString());
    return this.http.get<SimulatorCard[]>(`${this.basePath}/api/simulator/cards`, {
      params: params.keys().length > 0 ? params : undefined
    });
  }

  /**
   * Consulte le solde, le statut et l'historique de débit d'une carte spécifique.
   * Requiert un token (Terminal ou Admin).
   * GET /api/cards/{uid}?limit={limit}&offset={offset}
   */
  getCard(uid: string, limit = 50, offset = 0): Observable<CardDetail> {
    const params = new HttpParams()
      .set('limit', limit.toString())
      .set('offset', offset.toString());

    return this.http.get<CardDetail>(`${this.basePath}/api/cards/${encodeURIComponent(uid.trim())}`, { params });
  }

  /**
   * Consulte les journaux d'audit administratif d'une carte.
   * Requiert STRICTEMENT les droits Admin (HTTP 403 si token terminal).
   * GET /api/cards/{uid}/audit?limit={limit}&offset={offset}
   */
  getCardAudit(uid: string, limit = 50, offset = 0): Observable<CardAuditResponse> {
    const params = new HttpParams()
      .set('limit', limit.toString())
      .set('offset', offset.toString());

    return this.http.get<CardAuditResponse>(`${this.basePath}/api/cards/${encodeURIComponent(uid.trim())}/audit`, { params });
  }

  /**
   * Met à jour le solde (en millimes), le statut et/ou le libellé d'une carte avec motif d'audit.
   * Requiert STRICTEMENT les droits Admin (HTTP 403 si token terminal).
   * PUT /api/cards/{uid}
   */
  upsertCard(uid: string, request: CardUpsertRequest): Observable<TransitCard> {
    return this.http.put<TransitCard>(`${this.basePath}/api/cards/${encodeURIComponent(uid.trim())}`, request);
  }

  /**
   * Soumet une transaction de validation de transport au validateur RFID.
   * Requiert le token Terminal.
   * POST /api/validations
   */
  validate(request: ValidationRequest, options?: ValidationRequestOptions): Observable<ValidationResult> {
    if (!options?.skipAuth && !options?.skipErrorToast) {
      return this.http.post<ValidationResult>(`${this.basePath}/api/validations`, request);
    }

    let context = new HttpContext();
    let headers = new HttpHeaders();

    if (options.skipAuth) {
      context = context.set(SKIP_AUTH_CONTEXT, true);
      headers = headers.set(SKIP_AUTH_HEADER, 'true');
    }
    if (options.skipErrorToast) {
      context = context.set(SKIP_ERROR_TOAST_CONTEXT, true);
      headers = headers.set(SKIP_GLOBAL_ERROR_TOAST_HEADER, 'true');
    }

    return this.http.post<ValidationResult>(`${this.basePath}/api/validations`, request, { context, headers });
  }

  /**
   * Récupère la synthèse métier du tableau de bord via l'endpoint dédié GET /api/dashboard/summary
   * (sans exposer l'endpoint interne /metrics ni de secret de scraping Prometheus au navigateur).
   */
  getMetricsSummary(): Observable<PrometheusMetricsSummary> {
    return this.http.get<PrometheusMetricsSummary>(`${this.basePath}/api/dashboard/summary`).pipe(
      map(summary => ({
        ...summary,
        scrapedAt: summary.scrapedAt ? new Date(summary.scrapedAt) : new Date()
      }))
    );
  }

  /**
   * Analyseur strict et robuste des métriques Prometheus text-format.
   * Distingue clairement les métriques d'ingestion des données transactionnelles SQL.
   */
  private parsePrometheusMetrics(text: string): PrometheusMetricsSummary {
    let totalValidations = 0;
    let acceptedValidations = 0;
    let rejectedValidations = 0;
    let conflictValidations = 0;
    let totalDebitedMillimes = 0;

    const lines = text.split('\n');
    for (const line of lines) {
      if (!line || line.startsWith('#')) continue;

      // Métrique janus_validations_total
      if (line.startsWith('janus_validations_total{')) {
        const valueMatch = line.match(/\}\s+([\d.e+-]+)$/);
        const val = valueMatch ? parseFloat(valueMatch[1]) : 0;

        if (line.includes('decision="ACCEPTED"') && line.includes('duplicate="false"')) {
          acceptedValidations += val;
          totalValidations += val;
        } else if (line.includes('decision="REJECTED"') && line.includes('duplicate="false"')) {
          rejectedValidations += val;
          totalValidations += val;
        } else if (line.includes('decision="CONFLICT"')) {
          conflictValidations += val;
        }
      }

      // Métrique janus_fare_amount_millimes_total
      if (line.startsWith('janus_fare_amount_millimes_total{') || line.startsWith('janus_fare_amount_debited_millimes_total{')) {
        const valueMatch = line.match(/\}\s+([\d.e+-]+)$/);
        if (valueMatch) {
          totalDebitedMillimes += parseFloat(valueMatch[1]);
        }
      }
    }

    return {
      totalValidations,
      acceptedValidations,
      rejectedValidations,
      conflictValidations,
      totalDebitedMillimes,
      scrapedAt: new Date()
    };
  }
}
