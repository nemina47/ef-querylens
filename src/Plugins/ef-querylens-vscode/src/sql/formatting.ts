import { format as sqlFormatterFormat, type SqlLanguage } from 'sql-formatter';

import { QueryLensSqlDialect } from '../types';

export function formatSql(sql: string, sqlDialect: QueryLensSqlDialect): string {
    if (/^\s*--\s*=====\s*Split Query\s+/im.test(sql)) {
        return formatSplitQueryPreview(sql, sqlDialect);
    }

    return sql
        .split(/\n\s*-- next query --\s*\n/gi)
        .map(part => formatSingleStatement(part, sqlDialect))
        .join('\n\n-- next query --\n\n');
}

function formatSplitQueryPreview(sql: string, sqlDialect: QueryLensSqlDialect): string {
    const markerRegex = /^\s*--\s*=====\s*Split Query\s+.*?=====\s*$/gim;
    const markers = [...sql.matchAll(markerRegex)];
    if (markers.length === 0) {
        return formatSingleStatement(sql, sqlDialect);
    }

    const sections: string[] = [];
    for (let i = 0; i < markers.length; i++) {
        const marker = markers[i][0].trim();
        const start = markers[i].index! + markers[i][0].length;
        const end = i + 1 < markers.length ? markers[i + 1].index! : sql.length;
        const body = sql.slice(start, end).trim();
        const formattedBody = body.length > 0 ? formatSingleStatement(body, sqlDialect) : '';
        sections.push(formattedBody.length > 0 ? `${marker}\n${formattedBody}` : marker);
    }

    return sections.join('\n\n');
}

function formatSingleStatement(sql: string, sqlDialect: QueryLensSqlDialect): string {
    const trimmed = sql.trim();
    if (trimmed.length === 0) {
        return trimmed;
    }

    const formatterDialect = resolveSqlFormatterDialect(trimmed, sqlDialect);

    try {
        return sqlFormatterFormat(trimmed, {
            language: formatterDialect,
        }).trim();
    } catch {
        // Fallback keeps SQL readable if formatter parser cannot handle a provider-specific edge case.
        return legacySqlFormatter(trimmed);
    }
}

function legacySqlFormatter(sql: string): string {
    let formatted = sql.trim();
    const clausePatterns = [
        /\bWITH\b/gi,
        /\bSELECT\b/gi,
        /\bFROM\b/gi,
        /\bLEFT\s+JOIN\b/gi,
        /\bRIGHT\s+JOIN\b/gi,
        /\bINNER\s+JOIN\b/gi,
        /\bOUTER\s+JOIN\b/gi,
        /\bJOIN\b/gi,
        /\bWHERE\b/gi,
        /\bGROUP\s+BY\b/gi,
        /\bHAVING\b/gi,
        /\bORDER\s+BY\b/gi,
        /\bLIMIT\b/gi,
        /\bOFFSET\b/gi,
    ];

    for (const pattern of clausePatterns) {
        formatted = formatted.replace(pattern, '\n$&');
    }

    formatted = formatted
        .replace(/^\s*\n+/, '')
        .replace(/\n{3,}/g, '\n\n')
        .split('\n')
        .map(line => line.trimEnd())
        .join('\n');

    return formatted;
}

function resolveSqlFormatterDialect(sql: string, setting: QueryLensSqlDialect): SqlLanguage {
    if (setting !== 'auto') {
        return setting;
    }

    // Heuristic fallback for mixed providers: backticks => MySQL, brackets => SQL Server.
    if (sql.includes('`')) {
        return 'mysql';
    }

    if (sql.includes('[') && sql.includes(']')) {
        return 'transactsql';
    }

    return 'sql';
}
