import { Pipe, PipeTransform } from '@angular/core';

/**
 * Pipe de conversion et formatage strict des montants billettiques.
 * Les montants dans Janus sont toujours stockés en millimes (entiers).
 * 1 TND (Dinar Tunisien) = 1000 millimes.
 * Exemple : 1500 millimes -> "1,500 TND"
 */
@Pipe({
  name: 'millimes',
  standalone: true
})
export class MillimesPipe implements PipeTransform {
  transform(value: number | null | undefined, includeUnit = true): string {
    if (value === null || value === undefined || isNaN(value)) {
      return includeUnit ? '0,000 TND' : '0,000';
    }

    const dinars = value / 1000;
    // Format français avec séparateur de milliers et 3 décimales fixes
    const formatted = dinars.toLocaleString('fr-FR', {
      minimumFractionDigits: 3,
      maximumFractionDigits: 3
    });

    return includeUnit ? `${formatted} TND` : formatted;
  }
}
