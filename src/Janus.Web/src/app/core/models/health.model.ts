export interface ApiHealth {
  status: string;
  service: string;
}

export interface PrometheusMetricsSummary {
  totalValidations: number;
  acceptedValidations: number;
  rejectedValidations: number;
  conflictValidations: number;
  totalDebitedMillimes: number;
  activeCardsCount?: number;
  revokedCardsCount?: number;
  totalCardsCount?: number;
  totalCirculatingBalanceMillimes?: number;
  scrapedAt: Date;
}
