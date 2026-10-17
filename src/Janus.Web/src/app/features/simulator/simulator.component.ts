import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { JanusApiService } from '../../core/services/api.service';
import { SessionService } from '../../core/services/session.service';
import { ValidationSessionService } from '../../core/services/validation-session.service';
import { NotificationService } from '../../core/services/notification.service';
import { SimulatorCard } from '../../core/models/card.model';
import { ValidationRequest, ValidationResult, SimulatedValidationSessionEntry } from '../../core/models/validation.model';
import { BadgeComponent } from '../../shared/components/badge/badge.component';
import { MillimesPipe } from '../../shared/pipes/millimes.pipe';

@Component({
  selector: 'app-simulator',
  standalone: true,
  imports: [CommonModule, FormsModule, BadgeComponent, MillimesPipe],
  templateUrl: './simulator.component.html',
  styleUrls: ['./simulator.component.css']
})
export class SimulatorComponent implements OnInit {
  private readonly api = inject(JanusApiService);
  readonly session = inject(SessionService);
  readonly validationSession = inject(ValidationSessionService);
  private readonly notification = inject(NotificationService);

  readonly demoCards = signal<SimulatorCard[]>([]);
  readonly loadingCards = signal(false);
  readonly isProcessing = signal(false);

  selectedUid = '';
  customUid = '';
  useCustomUid = false;

  // Dernier résultat de validation pour affichage dynamique sur l'écran du terminal
  readonly currentResult = signal<ValidationResult | null>(null);
  readonly currentError = signal<{ status: number; error: string; message: string } | null>(null);
  readonly lastDurationMs = signal<number>(0);
  readonly lastRequestSent = signal<ValidationRequest | null>(null);

  // État du solde avant validation (renseigné si disponible)
  readonly balanceBefore = signal<number | null>(null);

  ngOnInit(): void {
    this.loadDemoCards();
  }

  loadDemoCards(): void {
    this.loadingCards.set(true);
    this.api.getSimulatorCards().subscribe({
      next: (cards) => {
        this.demoCards.set(cards);
        this.loadingCards.set(false);
        if (cards.length > 0 && !this.selectedUid) {
          this.selectedUid = cards[0].uid;
          this.balanceBefore.set(cards[0].balanceMillimes ?? null);
        }
      },
      error: () => {
        this.loadingCards.set(false);
        this.notification.error('Erreur chargement cartes', 'Impossible de récupérer les cartes du simulateur (vérifiez votre jeton Borne ou Admin).');
      }
    });
  }

  onCardChange(): void {
    const card = this.demoCards().find(c => c.uid === this.selectedUid);
    this.balanceBefore.set(card?.balanceMillimes ?? null);
  }

  get effectiveUid(): string {
    return this.useCustomUid ? this.customUid.trim().toUpperCase() : this.selectedUid;
  }

  /**
   * Scénario 1 : Validation standard avec génération d'un EventId unique (UUID v4).
   */
  simulateValidation(): void {
    const uid = this.effectiveUid;
    if (!uid) {
      this.notification.warning('Saisie requise', 'Veuillez sélectionner ou saisir un UID de carte.');
      return;
    }

    const eventId = this.generateUuidV4();
    const request: ValidationRequest = {
      eventId,
      cardUid: uid,
      occurredAt: new Date().toISOString(),
      eventType: 'TRANSPORT_VALIDATION'
    };

    this.executeValidationRequest(request, 'STANDARD');
  }

  /**
   * Scénario 2 : Répétition exacte de la dernière requête (Test d'idempotence).
   */
  simulateDuplicate(): void {
    const last = this.lastRequestSent();
    if (!last) {
      this.notification.warning('Aucune transaction préalable', 'Effectuez d\'abord une validation standard avant de tester le rejeu.');
      return;
    }

    this.executeValidationRequest(last, 'DUPLICATE_RETRY');
  }

  /**
   * Scénario 3 : Conflit d'idempotence (Même EventId avec payload modifié).
   */
  simulateConflict(): void {
    const last = this.lastRequestSent();
    if (!last) {
      this.notification.warning('Aucune transaction préalable', 'Effectuez d\'abord une validation standard avant de tester le conflit.');
      return;
    }

    // Réutilise le même EventId mais altère l'UID ou l'heure
    const conflictRequest: ValidationRequest = {
      eventId: last.eventId,
      cardUid: last.cardUid === '04A1B2C3D4E5F6' ? '04DEAD00000001' : '04A1B2C3D4E5F6',
      occurredAt: new Date(Date.now() + 5000).toISOString(),
      eventType: 'TRANSPORT_VALIDATION'
    };

    this.executeValidationRequest(conflictRequest, 'IDEMPOTENCY_CONFLICT');
  }

  /**
   * Scénario 4 : Test sans jeton de borne (Démonstration du contrôle de sécurité 401).
   */
  simulateUnauthorized(): void {
    const uid = this.effectiveUid;
    const eventId = this.generateUuidV4();
    const request: ValidationRequest = {
      eventId,
      cardUid: uid || '04A1B2C3D4E5F6',
      occurredAt: new Date().toISOString(),
      eventType: 'TRANSPORT_VALIDATION'
    };

    // Sauvegarde temporaire du jeton pour restauration immédiate
    const savedToken = this.session.terminalToken();
    this.session.setTerminalToken(null);

    this.executeValidationRequest(request, 'UNAUTHORIZED', () => {
      // Restauration du jeton
      this.session.setTerminalToken(savedToken);
    });
  }

  private executeValidationRequest(
    request: ValidationRequest,
    scenario: SimulatedValidationSessionEntry['scenario'],
    onComplete?: () => void
  ): void {
    this.isProcessing.set(true);
    this.currentResult.set(null);
    this.currentError.set(null);
    this.lastRequestSent.set(request);

    // Mémorise le solde actuel avant la requête s'il est disponible
    const cardBefore = this.demoCards().find(c => c.uid === request.cardUid);
    if (cardBefore) {
      this.balanceBefore.set(cardBefore.balanceMillimes ?? null);
    }

    const start = performance.now();

    this.api.validate(request).subscribe({
      next: (res) => {
        const duration = Math.round(performance.now() - start);
        this.lastDurationMs.set(duration);
        this.currentResult.set(res);
        this.isProcessing.set(false);

        // Enregistre l'événement dans le journal de session
        this.validationSession.addEntry({
          id: request.eventId,
          timestamp: new Date(),
          request,
          response: res,
          durationMs: duration,
          scenario
        });

        // Actualise la liste des cartes pour synchroniser le nouveau solde
        this.loadDemoCards();

        if (onComplete) onComplete();
      },
      error: (err) => {
        const duration = Math.round(performance.now() - start);
        this.lastDurationMs.set(duration);
        this.isProcessing.set(false);

        const errorInfo = {
          status: err.status || 0,
          error: err.error?.error || 'HTTP_ERROR',
          message: err.error?.message || err.message || 'Erreur lors de la validation'
        };
        this.currentError.set(errorInfo);

        this.validationSession.addEntry({
          id: request.eventId,
          timestamp: new Date(),
          request,
          error: errorInfo,
          durationMs: duration,
          scenario
        });

        if (onComplete) onComplete();
      }
    });
  }

  private generateUuidV4(): string {
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
      const r = Math.random() * 16 | 0;
      const v = c === 'x' ? r : (r & 0x3 | 0x8);
      return v.toString(16);
    });
  }
}
