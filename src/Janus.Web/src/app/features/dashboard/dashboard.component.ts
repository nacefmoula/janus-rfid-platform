import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { JanusApiService } from '../../core/services/api.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';
import { PrometheusMetricsSummary, ApiHealth } from '../../core/models/health.model';
import { TransitCard } from '../../core/models/card.model';
import { MillimesPipe } from '../../shared/pipes/millimes.pipe';
import { BadgeComponent } from '../../shared/components/badge/badge.component';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [CommonModule, RouterModule, MillimesPipe],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.css']
})
export class DashboardComponent implements OnInit {
  private readonly api = inject(JanusApiService);
  readonly validationSession = inject(ValidationSessionService);

  readonly isLoading = signal(true);
  readonly errorMessage = signal<string | null>(null);

  readonly metrics = signal<PrometheusMetricsSummary | null>(null);
  readonly health = signal<ApiHealth | null>(null);
  readonly cards = signal<TransitCard[]>([]);

  ngOnInit(): void {
    this.loadDashboardData();
  }

  loadDashboardData(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);

    // Charge en parallèle la santé et la synthèse métier (/api/dashboard/summary)
    this.api.getHealth().subscribe({
      next: (h) => this.health.set(h),
      error: () => this.health.set({ status: 'unreachable', service: 'janus-api' })
    });

    this.api.getMetricsSummary().subscribe({
      next: (m) => {
        this.metrics.set(m);
        this.isLoading.set(false);
      },
      error: () => {
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
