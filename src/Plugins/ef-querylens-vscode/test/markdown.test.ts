import { describe, expect, test } from 'vitest';

import { enableTrustedHoverCommands } from '../src/hover/markdown';
import { Hover, MarkdownString } from './mocks/vscode';

describe('hover markdown', () => {
    test('returns null when hover is missing', () => {
        expect(enableTrustedHoverCommands(null, ['efquerylens.copySql'])).toBeNull();
    });

    test('rewrites supported efquerylens links into command URIs', () => {
        const hover = new Hover([
            new MarkdownString('[Copy](efquerylens://copySql?uri=file:///c:/repo/query.cs&line=5&character=7)'),
            '[Open](efquerylens://openSqlEditor?uri=file:///c:/repo/query.cs&line=6&character=2)',
            { value: '[Recalc](efquerylens://recalculate?uri=file:///c:/repo/query.cs)' },
        ]);

        const trustedHover = enableTrustedHoverCommands(hover as unknown as import('vscode').Hover, ['efquerylens.copySql', 'efquerylens.recalculate']);
        expect(trustedHover).not.toBeNull();

        const markdownItems = (trustedHover?.contents ?? []) as MarkdownString[];
        expect(markdownItems).toHaveLength(3);
        expect(markdownItems[0].value).toContain('command:efquerylens.copySql?');
        expect(markdownItems[1].value).toContain('command:efquerylens.openSqlEditor?');
        expect(markdownItems[2].value).toContain('command:efquerylens.recalculate?');

        expect(markdownItems[0].isTrusted).toEqual({ enabledCommands: ['efquerylens.copySql', 'efquerylens.recalculate'] });
        expect(markdownItems[0].supportHtml).toBe(true);
        expect(markdownItems[0].supportThemeIcons).toBe(true);
    });

    test('preserves original link for unsupported host or missing uri parameter', () => {
        const hover = new Hover([
            new MarkdownString('[Unknown](efquerylens://unknownAction?uri=file:///c:/repo/query.cs)'),
            '[MissingUri](efquerylens://copySql?line=1&character=2)',
        ]);

        const trustedHover = enableTrustedHoverCommands(hover as unknown as import('vscode').Hover, ['efquerylens.copySql']);
        const markdownItems = (trustedHover?.contents ?? []) as MarkdownString[];

        expect(markdownItems[0].value).toContain('(efquerylens://unknownAction?uri=file:///c:/repo/query.cs)');
        expect(markdownItems[1].value).toContain('(efquerylens://copySql?line=1&character=2)');
    });

    test('retains baseUri and range for markdown strings', () => {
        const item = new MarkdownString('[Show](efquerylens://showSql?uri=file:///c:/repo/query.cs)');
        item.baseUri = 'file:///c:/repo';
        const hover = new Hover([item], { start: 1, end: 2 });

        const trustedHover = enableTrustedHoverCommands(hover as unknown as import('vscode').Hover, ['efquerylens.showSql']);
        const rewritten = trustedHover?.contents[0] as MarkdownString;

        expect(rewritten.baseUri).toBe('file:///c:/repo');
        expect(trustedHover?.range).toEqual({ start: 1, end: 2 });
        expect(rewritten.value).toContain('command:efquerylens.showSql?');
    });
});
