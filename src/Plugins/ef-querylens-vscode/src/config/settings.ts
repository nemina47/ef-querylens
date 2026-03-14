import { workspace } from 'vscode';

import { QueryLensSettings, QueryLensSqlDialect } from '../types';

export function readSettings(): QueryLensSettings {
    const config = workspace.getConfiguration('efquerylens');

    const maxCodeLensPerDocument = clampNumber(
        config.get<number>('codeLens.maxPerDocument', 50),
        1,
        500,
        50
    );

    const codeLensDebounceMs = clampNumber(
        config.get<number>('codeLens.debounceMs', 250),
        0,
        5000,
        250
    );

    const formatSqlOnShow = config.get<boolean>('sql.formatOnShow', true);
    const sqlDialect = readSqlDialect(config.get<string>('sql.dialect', 'auto'));
    const codeLensUseModelFilter = config.get<boolean>('codeLens.useModelFilter', false);
    const debugLogsEnabled = config.get<boolean>('debug.enableVerboseLogs', false);

    return {
        maxCodeLensPerDocument,
        codeLensDebounceMs,
        codeLensUseModelFilter,
        formatSqlOnShow,
        sqlDialect,
        debugLogsEnabled,
    };
}

function readSqlDialect(raw: string): QueryLensSqlDialect {
    switch (raw) {
        case 'sql':
        case 'mysql':
        case 'transactsql':
            return raw;
        default:
            return 'auto';
    }
}

function clampNumber(value: number | undefined, min: number, max: number, fallback: number): number {
    if (typeof value !== 'number' || Number.isNaN(value)) {
        return fallback;
    }

    if (value < min) {
        return min;
    }

    if (value > max) {
        return max;
    }

    return Math.floor(value);
}
