import { Injectable, signal, computed } from '@angular/core';
import { SimulatedValidationSessionEntry } from '../models/validation.model';

@Injectable({
  providedIn: 'root'
})
export class ValidationSessionService {
  private readonly entriesSignal = signal<SimulatedValidationSessionEntry[]>([]);

  readonly entries = computed(() => this.entriesSignal());
  readonly lastEntry = computed(() => this.entriesSignal()[0] ?? null);
  readonly count = computed(() => this.entriesSignal().length);

  addEntry(entry: SimulatedValidationSessionEntry): void {
    // Insère le plus récent au début
    this.entriesSignal.update(list => [entry, ...list]);
  }

  clear(): void {
    this.entriesSignal.set([]);
  }
}
