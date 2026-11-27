import { newOperationId } from './operation-id';

describe('newOperationId', () => {
  const uuidV4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

  it('génère un UUID v4 unique à chaque appel', () => {
    const ids = new Set(Array.from({ length: 50 }, () => newOperationId()));
    expect(ids.size).toBe(50);
    ids.forEach(id => expect(id).toMatch(uuidV4));
  });

  it('se replie sur crypto.getRandomValues lorsque randomUUID est indisponible (contexte HTTP non sécurisé)', () => {
    const realCrypto = globalThis.crypto;
    spyOnProperty(globalThis, 'crypto', 'get').and.returnValue({
      getRandomValues: (arr: Uint8Array<ArrayBuffer>) => realCrypto.getRandomValues(arr)
    } as unknown as Crypto);

    expect(newOperationId()).toMatch(uuidV4);
  });
});
