import * as path from 'path';
import { commands, env, ExtensionContext, Hover, MarkdownString, Uri, window, workspace } from 'vscode';
import { format as sqlFormatterFormat, type SqlLanguage } from 'sql-formatter';

import {
    LanguageClient,
    LanguageClientOptions,
    ServerOptions,
} from 'vscode-languageclient/node';

let client: LanguageClient;

export function activate(context: ExtensionContext) {
    // Let's resolve the path to the compiled DLL of the LSP server
    const serverPath = context.asAbsolutePath(
        path.join('..', '..', 'EFQueryLens.Lsp', 'bin', 'Debug', 'net10.0', 'EFQueryLens.Lsp.dll')
    );

    const settings = readSettings();

    const serverOptions: ServerOptions = {
        command: 'dotnet',
        args: [serverPath],
        options: {
            cwd: path.join(context.extensionPath, '..', '..'),
            env: {
                ...process.env,
                QUERYLENS_MAX_CODELENS_PER_DOCUMENT: String(settings.maxCodeLensPerDocument),
                QUERYLENS_CODELENS_DEBOUNCE_MS: String(settings.codeLensDebounceMs),
                QUERYLENS_CODELENS_USE_MODEL_FILTER: settings.codeLensUseModelFilter ? '1' : '0',
                QUERYLENS_DEBUG: settings.debugLogsEnabled ? '1' : '0',
            }
        }
    };

    const clientOptions: LanguageClientOptions = {
        // Register the server for plain C# documents
        documentSelector: [{ scheme: 'file', language: 'csharp' }],
        middleware: {
            provideCodeLenses: async (document, token, next) => {
                const start = Date.now();
                const result = await next(document, token);

                if (settings.debugLogsEnabled) {
                    const elapsed = Date.now() - start;
                    const count = Array.isArray(result) ? result.length : 0;
                    console.log(`[EFQueryLens] provideCodeLenses uri=${document.uri.toString()} count=${count} elapsedMs=${elapsed}`);
                }

                return result;
            },
            provideHover: async (document, position, token, next) => {
                const hover = await next(document, position, token);
                return enableTrustedHoverCommands(hover, ['efquerylens.copySql', 'efquerylens.showSql']);
            }
        },
        synchronize: {
            fileEvents: workspace.createFileSystemWatcher('**/*.cs')
        }
    };

    client = new LanguageClient(
        'efquerylens-lsp',
        'EF QueryLens Language Server',
        serverOptions,
        clientOptions
    );

    const showSqlCommand = commands.registerCommand(
        'efquerylens.showSql',
        async (uriInput: string, lineInput: number, characterInput: number) => {
            await showSqlFromLens(uriInput, lineInput, characterInput, settings.formatSqlOnShow, settings.sqlDialect, true);
        }
    );

    const copySqlCommand = commands.registerCommand(
        'efquerylens.copySql',
        async (uriInput: string, lineInput: number, characterInput: number) => {
            await copySqlFromLens(uriInput, lineInput, characterInput, settings.formatSqlOnShow, settings.sqlDialect);
        }
    );

    const showSqlFromCursorCommand = commands.registerCommand(
        'efquerylens.showSqlFromCursor',
        async () => {
            const location = getActiveEditorLocation();
            if (!location) {
                window.showInformationMessage('EF QueryLens: open a C# file and place the cursor on a query first.');
                return;
            }

            await showSqlFromLens(
                location.uri.toString(),
                location.line,
                location.character,
                settings.formatSqlOnShow,
                settings.sqlDialect,
                true);
        }
    );

    const copySqlFromCursorCommand = commands.registerCommand(
        'efquerylens.copySqlFromCursor',
        async () => {
            const location = getActiveEditorLocation();
            if (!location) {
                window.showInformationMessage('EF QueryLens: open a C# file and place the cursor on a query first.');
                return;
            }

            await copySqlFromLens(
                location.uri.toString(),
                location.line,
                location.character,
                settings.formatSqlOnShow,
                settings.sqlDialect);
        }
    );

    context.subscriptions.push(showSqlCommand);
    context.subscriptions.push(copySqlCommand);
    context.subscriptions.push(showSqlFromCursorCommand);
    context.subscriptions.push(copySqlFromCursorCommand);
    client.start();
}

export function deactivate(): Thenable<void> | undefined {
    if (!client) {
        return undefined;
    }
    return client.stop();
}

async function showSqlFromLens(
    uriInput: string,
    lineInput: number,
    characterInput: number,
    formatSqlOnShow: boolean,
    sqlDialect: QueryLensSqlDialect,
    includeCopyAction: boolean
): Promise<void> {
    const sqlText = await getSqlForLens(
        uriInput,
        lineInput,
        characterInput,
        formatSqlOnShow,
        sqlDialect,
        true);
    if (!sqlText) {
        return;
    }

    const uri = parseUri(uriInput);
    await openSqlPreviewInEditor(sqlText, uri?.fsPath);

    if (includeCopyAction) {
        const action = await window.showInformationMessage('EF QueryLens SQL ready', 'Copy SQL');
        if (action === 'Copy SQL') {
            await env.clipboard.writeText(sqlText);
            window.showInformationMessage('EF QueryLens: SQL copied to clipboard.');
        }
    }
}

async function openSqlPreviewInEditor(sqlText: string, sourceFilePath?: string): Promise<void> {
    const prefix = sourceFilePath
        ? `-- Source File: ${sourceFilePath}\n-- Generated By: EF QueryLens\n\n`
        : '-- Generated By: EF QueryLens\n\n';

    const document = await workspace.openTextDocument({
        language: 'sql',
        content: `${prefix}${sqlText}`,
    });

    await window.showTextDocument(document, {
        preview: true,
        preserveFocus: false,
    });
}

async function copySqlFromLens(
    uriInput: string,
    lineInput: number,
    characterInput: number,
    formatSqlOnShow: boolean,
    sqlDialect: QueryLensSqlDialect
): Promise<void> {
    const sqlText = await getSqlForLens(
        uriInput,
        lineInput,
        characterInput,
        formatSqlOnShow,
        sqlDialect,
        true);
    if (!sqlText) {
        return;
    }

    await env.clipboard.writeText(sqlText);
    window.showInformationMessage('EF QueryLens: SQL copied to clipboard.');
}

async function getSqlForLens(
    uriInput: string,
    lineInput: number,
    characterInput: number,
    formatSqlOnShow: boolean,
    sqlDialect: QueryLensSqlDialect,
    includeContextComments: boolean
): Promise<string | null> {
    if (!client) {
        return null;
    }

    const uri = parseUri(uriInput);
    if (!uri) {
        window.showWarningMessage('EF QueryLens: unable to resolve document URI for SQL preview.');
        return null;
    }

    try {
        const hover = await client.sendRequest('textDocument/hover', {
            textDocument: { uri: uri.toString() },
            position: {
                line: Number.isFinite(lineInput) ? lineInput : 0,
                character: Number.isFinite(characterInput) ? characterInput : 0,
            }
        });

        const hoverText = extractHoverText(hover);
        if (!hoverText) {
            window.showInformationMessage('EF QueryLens: no SQL preview available at this location.');
            return null;
        }

        const metadata = extractQueryLensMetadata(hoverText);
        const sqlText = extractSqlBlocks(hoverText);
        if (sqlText) {
            const formattedSql = formatSqlOnShow ? formatSql(sqlText, sqlDialect) : sqlText;
            return includeContextComments
                ? prependQueryLensContextComments(formattedSql, metadata)
                : formattedSql;
        }

        return hoverText;
    } catch (error) {
        const message = error instanceof Error ? error.message : String(error);
        window.showErrorMessage(`EF QueryLens: failed to retrieve SQL preview. ${message}`);
        return null;
    }
}

function parseUri(uriInput: unknown): Uri | null {
    if (typeof uriInput !== 'string' || uriInput.length === 0) {
        return null;
    }

    try {
        return Uri.parse(uriInput);
    } catch {
        return null;
    }
}

function getActiveEditorLocation(): { uri: Uri; line: number; character: number } | null {
    const editor = window.activeTextEditor;
    if (!editor) {
        return null;
    }

    const uri = editor.document.uri;
    if (uri.scheme !== 'file') {
        return null;
    }

    if (editor.document.languageId !== 'csharp') {
        return null;
    }

    const position = editor.selection.active;
    return {
        uri,
        line: position.line,
        character: position.character,
    };
}

function extractHoverText(hover: unknown): string {
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

function extractSqlBlocks(markdown: string): string | null {
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

function extractQueryLensMetadata(markdown: string): QueryLensHoverMetadata | null {
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
        };
    } catch {
        return null;
    }
}

function prependQueryLensContextComments(sql: string, metadata: QueryLensHoverMetadata | null): string {
    if (!metadata) {
        return sql;
    }

    const lines: string[] = [];
    lines.push('-- EF QueryLens Context');
    lines.push(`-- Mode: ${metadata.Mode}`);
    if (metadata.ModeDescription.trim().length > 0) {
        lines.push(`-- Mode Meaning: ${metadata.ModeDescription}`);
    }

    appendCommentedExpression(lines, 'Source LINQ', metadata.SourceExpression);
    if (metadata.SourceExpression !== metadata.ExecutedExpression) {
        appendCommentedExpression(lines, 'Executed LINQ', metadata.ExecutedExpression);
    }

    return `${lines.join('\n')}\n\n${sql}`;
}

function appendCommentedExpression(lines: string[], label: string, expression: string): void {
    if (expression.trim().length === 0) {
        return;
    }

    lines.push(`-- ${label}:`);
    for (const rawLine of expression.replace(/\r/g, '').split('\n')) {
        const line = rawLine.trimEnd();
        lines.push(line.length === 0 ? '--' : `-- ${line}`);
    }
}

function formatSql(sql: string, sqlDialect: QueryLensSqlDialect): string {
    return sql
        .split(/\n\s*-- next query --\s*\n/gi)
        .map(part => formatSingleStatement(part, sqlDialect))
        .join('\n\n-- next query --\n\n');
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

function readSettings(): {
    maxCodeLensPerDocument: number;
    codeLensDebounceMs: number;
    codeLensUseModelFilter: boolean;
    formatSqlOnShow: boolean;
    sqlDialect: QueryLensSqlDialect;
    debugLogsEnabled: boolean;
} {
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

type QueryLensSqlDialect = 'auto' | 'sql' | 'mysql' | 'transactsql';

type QueryLensHoverMetadata = {
    SourceExpression: string;
    ExecutedExpression: string;
    Mode: string;
    ModeDescription: string;
};

function enableTrustedHoverCommands(
    hover: Hover | null | undefined,
    enabledCommands: readonly string[]
): Hover | null {
    if (!hover) {
        return null;
    }

    const trusted = { enabledCommands: [...enabledCommands] };
    const contents = hover.contents.map(item => {
        if (item instanceof MarkdownString) {
            const markdown = new MarkdownString(item.value, item.supportThemeIcons);
            markdown.baseUri = item.baseUri;
            markdown.isTrusted = trusted;
            markdown.supportHtml = item.supportHtml;
            markdown.supportThemeIcons = item.supportThemeIcons;
            return markdown;
        }

        if (typeof item === 'string') {
            const markdown = new MarkdownString(item);
            markdown.isTrusted = trusted;
            return markdown;
        }

        return item;
    });

    return new Hover(contents, hover.range);
}
