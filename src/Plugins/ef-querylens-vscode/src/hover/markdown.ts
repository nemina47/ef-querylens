import { Hover, MarkdownString } from 'vscode';

import { QueryLensHoverMetadata } from '../types';
import { coerceNonNegativeInt } from '../utils/parsing';

export function extractHoverText(hover: unknown): string {
    if (!hover || typeof hover !== 'object') {
        return '';
    }

    const hoverValue = hover as { contents?: unknown };
    const contents = hoverValue.contents;

    if (!contents) {
        return '';
    }

    if (typeof contents === 'string') {
        return contents;
    }

    if (Array.isArray(contents)) {
        return contents
            .map(formatMarkedString)
            .filter(Boolean)
            .join('\n\n');
    }

    if (typeof contents === 'object') {
        const markup = contents as { kind?: unknown; value?: unknown };
        if (typeof markup.value === 'string') {
            return markup.value;
        }
    }

    return '';
}

export function extractSqlBlocks(markdown: string): string | null {
    const matches = [...markdown.matchAll(/```sql\s*([\s\S]*?)```/gi)];
    if (matches.length === 0) {
        return null;
    }

    const sqlBlocks = matches
        .map(match => (match[1] ?? '').trim())
        .filter(block => block.length > 0);

    if (sqlBlocks.length === 0) {
        return null;
    }

    return sqlBlocks.join('\n\n-- next query --\n\n');
}

export function extractQueryLensMetadata(markdown: string): QueryLensHoverMetadata | null {
    const match = markdown.match(/<!--\s*QUERYLENS_META:([A-Za-z0-9+/=]+)\s*-->/i);
    if (!match || !match[1]) {
        return null;
    }

    try {
        const decoded = Buffer.from(match[1], 'base64').toString('utf8');
        const parsed = JSON.parse(decoded) as Partial<QueryLensHoverMetadata>;
        if (typeof parsed.SourceExpression !== 'string' || typeof parsed.ExecutedExpression !== 'string') {
            return null;
        }

        return {
            SourceExpression: parsed.SourceExpression,
            ExecutedExpression: parsed.ExecutedExpression,
            Mode: typeof parsed.Mode === 'string' ? parsed.Mode : 'direct',
            ModeDescription: typeof parsed.ModeDescription === 'string' ? parsed.ModeDescription : '',
            Warnings: Array.isArray(parsed.Warnings)
                ? parsed.Warnings.filter((item): item is string => typeof item === 'string')
                : [],
            SourceFile: typeof parsed.SourceFile === 'string' ? parsed.SourceFile : '',
            SourceLine: typeof parsed.SourceLine === 'number' ? parsed.SourceLine : 0,
            DbContextType: typeof parsed.DbContextType === 'string' ? parsed.DbContextType : '',
            ProviderName: typeof parsed.ProviderName === 'string' ? parsed.ProviderName : '',
            CreationStrategy: typeof parsed.CreationStrategy === 'string' ? parsed.CreationStrategy : '',
        };
    } catch {
        return null;
    }
}

export function prependQueryLensContextComments(sql: string, metadata: QueryLensHoverMetadata | null): string {
    if (!metadata) {
        return sql;
    }

    const lines: string[] = [];
    lines.push('-- EF QueryLens');

    if (metadata.SourceFile) {
        const lineDisplay = metadata.SourceLine > 0 ? `, line ${metadata.SourceLine}` : '';
        lines.push(`-- Source:    ${metadata.SourceFile}${lineDisplay}`);
    }

    appendCommentedExpression(lines, 'LINQ', metadata.SourceExpression);

    if (metadata.SourceExpression !== metadata.ExecutedExpression && metadata.ExecutedExpression.trim().length > 0) {
        appendCommentedExpression(lines, 'Executed LINQ (differs from source)', formatLinqExpressionForComments(metadata.ExecutedExpression));
    }

    if (metadata.DbContextType) {
        lines.push(`-- DbContext: ${metadata.DbContextType}`);
    }
    if (metadata.ProviderName) {
        lines.push(`-- Provider:  ${metadata.ProviderName}`);
    }
    if (metadata.CreationStrategy) {
        lines.push(`-- Strategy:  ${metadata.CreationStrategy}`);
    }

    if (metadata.Warnings.length > 0) {
        lines.push('-- Notes:');
        for (const warning of metadata.Warnings) {
            lines.push(`--   ${warning}`);
        }
    }

    return `${lines.join('\n')}\n\n${sql}`;
}

export function enableTrustedHoverCommands(
    hover: Hover | null | undefined,
    enabledCommands: readonly string[]
): Hover | null {
    if (!hover) {
        return null;
    }

    const trusted = { enabledCommands: [...enabledCommands] };
    const contents = hover.contents.map(item => {
        if (item instanceof MarkdownString) {
            const markdown = new MarkdownString(rewriteQueryLensActionLinks(item.value), item.supportThemeIcons);
            markdown.baseUri = item.baseUri;
            markdown.isTrusted = trusted;
            markdown.supportHtml = item.supportHtml;
            markdown.supportThemeIcons = item.supportThemeIcons;
            return markdown;
        }

        if (typeof item === 'string') {
            const markdown = new MarkdownString(rewriteQueryLensActionLinks(item));
            markdown.isTrusted = trusted;
            return markdown;
        }

        if (typeof item === 'object' && item !== null && 'value' in item) {
            const value = (item as { value?: unknown }).value;
            if (typeof value === 'string') {
                const markdown = new MarkdownString(rewriteQueryLensActionLinks(value));
                markdown.isTrusted = trusted;
                return markdown;
            }
        }

        return item;
    });

    return new Hover(contents, hover.range);
}

function formatMarkedString(value: unknown): string {
    if (typeof value === 'string') {
        return value;
    }

    if (value && typeof value === 'object') {
        const marked = value as { language?: unknown; value?: unknown };
        if (typeof marked.value === 'string' && typeof marked.language === 'string') {
            return `\`\`\`${marked.language}\n${marked.value}\n\`\`\``;
        }
    }

    return '';
}

function appendCommentedExpression(lines: string[], label: string, expression: string): void {
    if (expression.trim().length === 0) {
        return;
    }

    const normalizedLabel = label.trim();
    const header = normalizedLabel.includes(':') || normalizedLabel.endsWith(':')
        ? normalizedLabel
        : `${normalizedLabel}:`;
    lines.push(`-- ${header}`);
    for (const rawLine of expression.replace(/\r/g, '').split('\n')) {
        const line = rawLine.trimEnd();
        lines.push(line.length === 0 ? '--' : `-- ${line}`);
    }
}

function formatLinqExpressionForComments(expression: string): string {
    const trimmed = expression.replace(/\r/g, '').trim();
    if (trimmed.length === 0) {
        return trimmed;
    }

    return trimmed.replace(
        /\.(AsNoTracking|AsTracking|AsQueryable|Where|SelectMany|Select|Include|ThenInclude|OrderByDescending|OrderBy|ThenByDescending|ThenBy|Skip|Take|GroupBy|Join|Any|Count|LongCount|Distinct)\s*\(/g,
        '\n    .$1(');
}

function rewriteQueryLensActionLinks(markdown: string): string {
    return markdown.replace(/\(efquerylens:\/\/([a-zA-Z]+)\?([^)]+)\)/g, (full, hostRaw, queryRaw) => {
        const commandUri = toQueryLensCommandUri(String(hostRaw), String(queryRaw));
        return commandUri ? `(${commandUri})` : full;
    });
}

function toQueryLensCommandUri(hostRaw: string, queryRaw: string): string | null {
    const host = hostRaw.toLowerCase();
    const commandId = host === 'copysql'
        ? 'efquerylens.copySql'
        : host === 'opensqleditor'
            ? 'efquerylens.openSqlEditor'
            : host === 'showsql'
                ? 'efquerylens.showSql'
                : null;

    if (!commandId) {
        return null;
    }

    const params = new URLSearchParams(queryRaw);
    const uri = params.get('uri');
    if (!uri) {
        return null;
    }

    const line = coerceNonNegativeInt(params.get('line'), 0);
    const character = coerceNonNegativeInt(params.get('character'), 0);
    const encodedArgs = encodeURIComponent(JSON.stringify([uri, line, character]));
    return `command:${commandId}?${encodedArgs}`;
}
