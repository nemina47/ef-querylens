import { Hover, MarkdownString } from 'vscode';

import { coerceNonNegativeInt } from '../utils/parsing';

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
            const markdown = new MarkdownString(rewriteQueryLensActionLinks(item.value));
            markdown.baseUri = item.baseUri;
            markdown.isTrusted = trusted;
            markdown.supportHtml = true;
            markdown.supportThemeIcons = true;
            return markdown;
        }

        if (typeof item === 'string') {
            const markdown = new MarkdownString(rewriteQueryLensActionLinks(item));
            markdown.isTrusted = trusted;
            markdown.supportHtml = true;
            markdown.supportThemeIcons = true;
            return markdown;
        }

        if (typeof item === 'object' && item !== null && 'value' in item) {
            const value = (item as { value?: unknown }).value;
            if (typeof value === 'string') {
                const markdown = new MarkdownString(rewriteQueryLensActionLinks(value));
                markdown.isTrusted = trusted;
                markdown.supportHtml = true;
                markdown.supportThemeIcons = true;
                return markdown;
            }
        }

        return item;
    });

    return new Hover(contents, hover.range);
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
        : host === 'opensql' || host === 'opensqleditor'
            ? 'efquerylens.openSqlEditor'
            : host === 'showsql'
                ? 'efquerylens.showSql'
                : host === 'recalculate'
                    ? 'efquerylens.recalculate'
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
