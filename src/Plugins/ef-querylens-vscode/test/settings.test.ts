import { beforeEach, describe, expect, test } from 'vitest';

import { resetConfigurationReader, setConfigurationReader } from './mocks/vscode';

import { readSettings } from '../src/config/settings';

describe('settings', () => {
    beforeEach(() => {
        resetConfigurationReader();
    });

    test('returns defaults and normalizes dialect when config is missing', () => {
        setConfigurationReader(<T,>(_: string, defaultValue: T) => defaultValue);

        const settings = readSettings();

        expect(settings.maxCodeLensPerDocument).toBe(50);
        expect(settings.codeLensDebounceMs).toBe(250);
        expect(settings.sqlDialect).toBe('auto');
        expect(settings.formatSqlOnShow).toBe(true);
        expect(settings.codeLensUseModelFilter).toBe(false);
        expect(settings.debugLogsEnabled).toBe(false);
    });

    test('clamps numeric values and floors decimals', () => {
        setConfigurationReader(<T,>(key: string, defaultValue: T) => {
            switch (key) {
                case 'codeLens.maxPerDocument':
                    return 999 as T;
                case 'codeLens.debounceMs':
                    return 12.9 as T;
                case 'sql.dialect':
                    return 'mysql' as T;
                default:
                    return defaultValue;
            }
        });

        const settings = readSettings();

        expect(settings.maxCodeLensPerDocument).toBe(500);
        expect(settings.codeLensDebounceMs).toBe(12);
        expect(settings.sqlDialect).toBe('mysql');
    });

    test('falls back for non-numeric values and unknown dialects', () => {
        setConfigurationReader(<T,>(key: string, defaultValue: T) => {
            switch (key) {
                case 'codeLens.maxPerDocument':
                    return Number.NaN as T;
                case 'codeLens.debounceMs':
                    return undefined as T;
                case 'sql.dialect':
                    return 'oracle' as T;
                default:
                    return defaultValue;
            }
        });

        const settings = readSettings();

        expect(settings.maxCodeLensPerDocument).toBe(50);
        expect(settings.codeLensDebounceMs).toBe(250);
        expect(settings.sqlDialect).toBe('auto');
    });
});
