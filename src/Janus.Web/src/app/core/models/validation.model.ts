export type ValidationDecision = 'ACCEPTED' | 'REJECTED';

export type ValidationReason =
  | 'ACTIVE_CARD'
  | 'CARD_REVOKED'
  | 'INSUFFICIENT_FUNDS'
  | 'UNKNOWN_CARD'
  | 'PASSBACK_VIOLATION'
  | 'IDEMPOTENCY_CONFLICT'
  | 'INVALID_REQUEST'
  | string;

export interface ValidationRequest {
  eventId: string;
  cardUid: string;
  occurredAt?: string;
  eventType: 'TRANSPORT_VALIDATION';
}

export interface ValidationResult {
  eventId: string;
  terminalId: string;
  cardUid: string;
  decision: ValidationDecision;
  reason: ValidationReason;
  balanceMillimes?: number;
  amountDebited?: number;
  duplicate: boolean;
  receivedAt: string;
}

export interface SimulatedValidationSessionEntry {
  id: string;
  timestamp: Date;
  request: ValidationRequest;
  response?: ValidationResult;
  error?: {
    status: number;
    error: string;
    message: string;
  };
  durationMs: number;
  scenario: 'STANDARD' | 'DUPLICATE_RETRY' | 'IDEMPOTENCY_CONFLICT' | 'UNAUTHORIZED';
}
