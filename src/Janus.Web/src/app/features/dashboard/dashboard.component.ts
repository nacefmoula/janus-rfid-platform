import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { Subscription } from 'rxjs';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService, UserRole } from '../../core/services/session.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';
import { PrometheusMetricsSummary, ApiHealth } from '../../core/models/health.model';
import { MillimesPipe } from '../../shared/pipes/millimes.pipe';

export type DatabaseStatusState = 'OPERATIONAL' | 'UNAVAILABLE' | 'UNVERIFIED' | 'CHECKING';

export interface DatabaseStatusInfo {
  state: DatabaseStatusState;
  label: string;
  isOperational: boolean;
  isError: boolean;
}

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterModule, MillimesPipe],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.css']
})
export class DashboardComponent implements OnInit, OnDestroy {
  private readonly api = inject(JanusApiService);
  readonly session = inject(SessionService);
  readonly validationSession = inject(ValidationSessionService);

  readonly isLoading = signal(true);
  readonly errorMessage = signal<string | null>(null);
  readonly summaryError = signal<HttpErrorResponse | Error | null>(null);

  readonly metrics = signal<PrometheusMetricsSummary | null>(null);
  readonly health = signal<ApiHealth | null>(null);

  private sessionSub?: Subscription;
  private healthSub?: Subscription;
  private summarySub?: Subscription;

  ngOnInit(): void {
    this.loadDashboardData();
    this.sessionSub = this.session.sessionChanges$.subscribe((role) => {
      this.onSessionChanged(role);
    });
  }

  ngOnDestroy(): void {
    this.sessionSub?.unsubscribe();
    this.healthSub?.unsubscribe();
    this.summarySub?.unsubscribe();
  }

  private onSessionChanged(role: UserRole): void {
    this.healthSub?.unsubscribe();
    this.summarySub?.unsubscribe();
    this.metrics.set(null);
    this.summaryError.set(null);
    this.errorMessage.set(null);
    if (role === 'ANONYMOUS') {
      this.isLoading.set(false);
      return;
    }
    this.loadDashboardData();
  }

  loadDashboardData(): void {
    this.healthSub?.unsubscribe();
    this.summarySub?.unsubscribe();

    const expectedVersion = this.session.sessionVersion();
    this.errorMessage.set(null);
    this.summaryError.set(null);

    // Charge en parallèle la santé de l'API (accessible publiquement)
    this.healthSub = this.api.getHealth().subscribe({
      next: (h) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.health.set(h);
      },
      error: () => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.health.set({ status: 'unreachable', service: 'janus-api' });
      }
    });

    // En mode ANONYMOUS, n'émet aucun appel vers /api/dashboard/summary (réservé aux rôles Terminal et Admin)
    if (this.session.activeRole() === 'ANONYMOUS') {
      this.metrics.set(null);
      this.isLoading.set(false);
      return;
    }

    this.isLoading.set(true);

    this.summarySub = this.api.getMetricsSummary().subscribe({
      next: (m) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.metrics.set(m);
        this.summaryError.set(null);
        this.isLoading.set(false);
      },
      error: (err: unknown) => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.metrics.set(null);
        this.summaryError.set(err instanceof HttpErrorResponse ? err : (err instanceof Error ? err : new Error(String(err))));
        this.isLoading.set(false);
        this.errorMessage.set('Impossible de charger la synthèse d\'exploitation (/api/dashboard/summary). Vérifiez votre jeton de session (Borne ou Admin).');
      }
    });
  }

  get postgresStatus(): DatabaseStatusInfo {
    if (this.session.activeRole() === 'ANONYMOUS') {
      return {
        state: 'UNVERIFIED',
        label: 'Non vérifié (session requise)',
        isOperational: false,
        isError: false
      };
    }

    if (this.metrics()) {
      return {
        state: 'OPERATIONAL',
        label: 'Opérationnel (vérifié via SQL)',
        isOperational: true,
        isError: false
      };
    }

    const err = this.summaryError();
    if (err) {
      const status = err instanceof HttpErrorResponse ? err.status : (typeof (err as any)?.status === 'number' ? (err as any).status : undefined);
      if (status === 401 || status === 403) {
        return {
          state: 'UNVERIFIED',
          label: 'Non vérifié (droits insuffisants)',
          isOperational: false,
          isError: false
        };
      }
      return {
        state: 'UNAVAILABLE',
        label: 'Indisponible (échec requête SQL)',
        isOperational: false,
        isError: true
      };
    }

    if (this.isLoading()) {
      return {
        state: 'CHECKING',
        label: 'Vérification en cours...',
        isOperational: false,
        isError: false
      };
    }

    return {
      state: 'UNVERIFIED',
      label: 'Non vérifié',
      isOperational: false,
      isError: false
    };
  }

  get acceptanceRate(): number {
    const m = this.metrics();
    if (!m || m.totalValidations === 0) return 0;
    return Math.round((m.acceptedValidations / m.totalValidations) * 100);
  }

  get activeCardsCount(): number {
    return this.metrics()?.activeCardsCount ?? 0;
  }

  get revokedCardsCount(): number {
    return this.metrics()?.revokedCardsCount ?? 0;
  }

  get totalCardsCount(): number {
    return this.metrics()?.totalCardsCount ?? 0;
  }

  get totalCirculatingBalanceMillimes(): number {
    return this.metrics()?.totalCirculatingBalanceMillimes ?? 0;
  }
}
