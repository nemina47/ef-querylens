import { describe, expect, test } from 'vitest';

import { clamp, coerceNonNegativeInt, parseUri } from '../src/utils/parsing';

describe('parsing utilities', () => {
    test('parseUri returns null for non-string and invalid input', () => {
        expect(parseUri(undefined)).toBeNull();
        expect(parseUri('')).toBeNull();
        expect(parseUri('@@@not a uri@@@')).toBeNull();
    });

    test('parseUri returns Uri for valid input', () => {
        const value = parseUri('file:///c:/repo/app/Program.cs');
        expect(value).not.toBeNull();
        expect(value?.scheme).toBe('file');
    });

    test('clamp constrains finite values and falls back for non-finite values', () => {
        expect(clamp(10, 0, 5)).toBe(5);
        expect(clamp(-1, 0, 5)).toBe(0);
        expect(clamp(3, 0, 5)).toBe(3);
        expect(clamp(Number.NaN, 2, 10)).toBe(2);
    });

    test('coerceNonNegativeInt handles numbers and numeric strings', () => {
        expect(coerceNonNegativeInt(9.8, 0)).toBe(9);
        expect(coerceNonNegativeInt(-4, 7)).toBe(0);
        expect(coerceNonNegativeInt(' 42 ', 1)).toBe(42);
        expect(coerceNonNegativeInt(' -10 ', 1)).toBe(0);
    });

    test('coerceNonNegativeInt returns fallback for unsupported values', () => {
        expect(coerceNonNegativeInt('', 8)).toBe(8);
        expect(coerceNonNegativeInt({}, 8)).toBe(8);
        expect(coerceNonNegativeInt('nope', 8)).toBe(8);
    });
});
