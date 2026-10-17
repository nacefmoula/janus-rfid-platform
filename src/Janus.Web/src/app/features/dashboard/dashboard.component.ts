import { Component, inject, OnInit, OnDestroy, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { Subscription } from 'rxjs';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService, UserRole } from '../../core/services/session.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';
import { PrometheusMetricsSummary, ApiHealth } from '../../core/models/health.model';
import { TransitCard } from '../../core/models/card.model';
import { MillimesPipe } from '../../shared/pipes/millimes.pipe';

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

  readonly metrics = signal<PrometheusMetricsSummary | null>(null);
  readonly health = signal<ApiHealth | null>(null);
  readonly cards = signal<TransitCard[]>([]);

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
    this.cards.set([]);
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
        this.isLoading.set(false);
      },
      error: () => {
        if (this.session.sessionVersion() !== expectedVersion) return;
        this.metrics.set(null);
        this.isLoading.set(false);
        this.errorMessage.set('Impossible de charger la synthèse d\'exploitation (/api/dashboard/summary). Vérifiez votre jeton de session (Borne ou Admin).');
      }
    });
  }

  get acceptanceRate(): number {
    const m = this.metrics();
    if (!m || m.totalValidations === 0) return 0;
    return Math.round((m.acceptedValidations / m.totalValidations) * 100);
  }

  get activeCardsCount(): number {
    return this.metrics()?.activeCardsCount ?? this.cards().filter(c => c.status.toString().toUpperCase() === 'ACTIVE').length;
  }

  get revokedCardsCount(): number {
    return this.metrics()?.revokedCardsCount ?? this.cards().filter(c => c.status.toString().toUpperCase() === 'REVOKED').length;
  }

  get totalCardsCount(): number {
    return this.metrics()?.totalCardsCount ?? this.cards().length;
  }

  get totalCirculatingBalanceMillimes(): number {
    return this.metrics()?.totalCirculatingBalanceMillimes ?? this.cards().reduce((sum, c) => sum + (c.balanceMillimes || 0), 0);
  }
}
