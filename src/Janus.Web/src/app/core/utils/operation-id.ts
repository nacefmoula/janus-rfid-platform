/**
 * Génère un identifiant d'opération (UUID v4) pour les écritures idempotentes (ex. crédit de carte).
 *
 * `crypto.randomUUID()` n'est exposé que dans un contexte sécurisé (HTTPS ou localhost) ; lorsque l'interface
 * est servie en HTTP sur une adresse réseau, on se replie sur `crypto.getRandomValues()` (disponible partout)
 * pour conserver un aléa cryptographique. Aucun repli sur Math.random() : une collision d'operationId
 * serait interprétée par le serveur comme un rejeu.
 */
export function newOperationId(): string {
  const c: Crypto | undefined = globalThis.crypto;
  if (c && typeof c.randomUUID === 'function') {
    return c.randomUUID();
  }
  if (!c || typeof c.getRandomValues !== 'function') {
    throw new Error('Secure random generator unavailable: cannot create an idempotent operation id.');
  }

  const bytes = c.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // variante RFC 4122
  const hex = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
