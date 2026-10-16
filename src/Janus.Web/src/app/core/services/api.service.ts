import { Injectable, inject } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, map } from 'rxjs';
import { ApiHealth, PrometheusMetricsSummary } from '../models/health.model';
import { TransitCard, CardDetail, CardUpsertRequest, CardAuditResponse } from '../models/card.model';
import { ValidationRequest, ValidationResult } from '../models/validation.model';

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
   * Récupère la liste de démonstration des cartes enregistrées.
   * Endpoint public de démonstration : GET /api/demo/cards
   */
  getDemoCards(): Observable<TransitCard[]> {
    return this.http.get<TransitCard[]>(`${this.basePath}/api/demo/cards`);
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
  validate(request: ValidationRequest): Observable<ValidationResult> {
    return this.http.post<ValidationResult>(`${this.basePath}/api/validations`, request);
  }

  /**
   * Récupère les métriques d'observabilité brutes exposées par OpenTelemetry /metrics
   * et les convertit en résumé synthétique.
   */
  getMetricsSummary(): Observable<PrometheusMetricsSummary> {
    return this.http.get(`${this.basePath}/metrics`, { responseType: 'text' }).pipe(
      map(rawMetrics => this.parsePrometheusMetrics(rawMetrics))
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
