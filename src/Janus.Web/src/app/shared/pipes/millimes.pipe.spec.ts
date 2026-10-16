import { MillimesPipe } from './millimes.pipe';

describe('MillimesPipe', () => {
  const pipe = new MillimesPipe();

  it('devrait convertir 1500 millimes en 1,500 TND', () => {
    expect(pipe.transform(1500)).toBe('1,500 TND');
  });

  it('devrait convertir 10000 millimes en 10,000 TND', () => {
    expect(pipe.transform(10000)).toBe('10,000 TND');
  });

  it('devrait convertir 0 millimes en 0,000 TND', () => {
    expect(pipe.transform(0)).toBe('0,000 TND');
  });

  it('devrait gérer les valeurs null ou undefined en renvoyant 0,000 TND', () => {
    expect(pipe.transform(null)).toBe('0,000 TND');
    expect(pipe.transform(undefined)).toBe('0,000 TND');
  });

  it('devrait permettre d\'exclure l\'unité TND si demandé', () => {
    expect(pipe.transform(2500, false)).toBe('2,500');
  });
});
