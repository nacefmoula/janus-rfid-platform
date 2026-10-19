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
  balanceMillimes: number;
  status?: CardStatus | string;
  label?: string;
  reason?: string;
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
