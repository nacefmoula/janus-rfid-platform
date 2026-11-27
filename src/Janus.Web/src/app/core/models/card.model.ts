export type CardStatus = 'Active' | 'Revoked';

export interface SimulatorCard {
  uid: string;
  status: CardStatus | string;
  label: string;
  balanceMillimes?: number;
}

export interface TransitCard extends SimulatorCard {
  balanceMillimes?: number;
}

export interface FareTransaction {
  id: number;
  terminalId: string;
  eventId: string;
  amountMillimes: number;
  balanceAfterMillimes: number;
  createdAt: string;
}

export interface CardDetail {
  uid: string;
  status: CardStatus | string;
  label: string;
  balanceMillimes: number;
  transactionsCount: number;
  limit: number;
  offset: number;
  transactions: FareTransaction[];
}

export interface CardUpsertRequest {
  /**
   * Solde absolu : à fournir à la création. Sur une carte existante, omis = solde inchangé ;
   * une correction absolue exige `expectedBalanceMillimes` (428 sinon). Les recharges passent par `creditCard`.
   */
  balanceMillimes?: number;
  status?: CardStatus | string;
  label?: string;
  reason?: string;
  /**
   * Garde de concurrence optimiste (LOT-3B) : solde sur lequel l'administrateur s'est basé.
   * Le serveur répond 409 `stale_balance` si le solde a changé depuis (ex. débit d'un terminal).
   */
  expectedBalanceMillimes?: number;
}

/** Crédit idempotent (LOT-3B/3D) : l'operationId est réutilisé tel quel en cas de nouvel essai. */
export interface CardCreditRequest {
  operationId: string;
  amountMillimes: number;
  reason: string;
}

export interface CardCreditResult {
  uid: string;
  operationId: string;
  amountMillimes: number;
  balanceAfterMillimes: number;
  duplicate: boolean;
  createdAt: string;
}

export interface CardAuditLog {
  id: number;
  action: string;
  previousBalanceMillimes: number | null;
  newBalanceMillimes: number;
  previousStatus: string | null;
  newStatus: string;
  reason: string | null;
  actor: string;
  createdAt: string;
}

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface CardAuditResponse {
  cardUid: string;
  totalCount: number;
  limit: number;
  offset: number;
  auditLogs: CardAuditLog[];
  items?: CardAuditLog[];
  page?: number;
  pageSize?: number;
  totalPages?: number;
}
