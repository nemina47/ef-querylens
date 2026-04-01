type ConfigReader = <T>(key: string, defaultValue: T) => T;
type CommandHandler = (...args: unknown[]) => unknown;
type ConfigChangeHandler = (event: { affectsConfiguration: (section: string) => boolean }) => void | Promise<void>;

type MockLine = { text: string };
type MockDocument = {
    uri: unknown;
    lineCount: number;
    lineAt: (line: number) => MockLine;
};
type MockEditor = {
    selection: unknown;
    revealRange: (_range: unknown, _type?: unknown) => void;
};

class MockUri {
    constructor(
        public readonly scheme: string,
        public readonly value: string
    ) {}

    static parse(uriInput: string): MockUri {
        const parsed = new URL(uriInput);
        return new MockUri(parsed.protocol.replace(':', ''), uriInput);
    }

    static file(filePath: string): MockUri {
        const normalized = filePath.replace(/\\/g, '/');
        const prefixed = normalized.startsWith('/') ? normalized : `/${normalized}`;
        return new MockUri('file', `file://${prefixed}`);
    }

    static joinPath(base: MockUri, ...segments: string[]): MockUri {
        const baseValue = base.toString().replace(/\/+$/, '');
        const joined = segments
            .map(segment => segment.replace(/^\/+|\/+$/g, ''))
            .filter(segment => segment.length > 0)
            .join('/');
        const value = joined.length > 0 ? `${baseValue}/${joined}` : baseValue;
        return new MockUri(base.scheme, value);
    }

    toString(): string {
        return this.value;
    }
}

let configReader: ConfigReader = <T,>(_: string, defaultValue: T) => defaultValue;
const commandHandlers = new Map<string, CommandHandler>();
const executedCommands: Array<{ id: string; args: unknown[] }> = [];
const warningMessages: string[] = [];
const infoMessages: string[] = [];
const errorMessages: string[] = [];
let nextWarningChoice: string | undefined;
let nextInfoChoice: string | undefined;
let nextSaveDialogUri: unknown | null = null;
let clipboardText = '';
let documentText = 'var query = context.Entities.Where(e => e.Id > 0);';
const openedDocuments: unknown[] = [];
const shownDocuments: Array<{ document: unknown; options: unknown }> = [];
const appliedEdits: unknown[] = [];
const createdEdits: Array<{ uri: unknown; position: unknown; text: string }> = [];
const createdFiles: Array<{ uri: unknown; options: unknown }> = [];
let outputShown = false;
const outputLines: string[] = [];
let onDidChangeConfigurationHandler: ConfigChangeHandler | undefined;

export const Uri = MockUri;

export const commands = {
    registerCommand: (id: string, handler: CommandHandler) => {
        commandHandlers.set(id, handler);
        return {
            dispose: () => {
                commandHandlers.delete(id);
            },
        };
    },
    executeCommand: async (id: string, ...args: unknown[]) => {
        executedCommands.push({ id, args });
        const handler = commandHandlers.get(id);
        if (!handler) {
            return undefined;
        }

        return handler(...args);
    },
};

export const window = {
    createOutputChannel,
    showWarningMessage: async (message: string, ..._items: string[]) => {
        warningMessages.push(message);
        return nextWarningChoice;
    },
    showInformationMessage: async (message: string, ..._items: string[]) => {
        infoMessages.push(message);
        return nextInfoChoice;
    },
    showErrorMessage: async (message: string) => {
        errorMessages.push(message);
        return undefined;
    },
    showSaveDialog: async () => nextSaveDialogUri,
    showTextDocument: async (_doc: unknown, _options?: unknown) => {
        shownDocuments.push({ document: _doc, options: _options });
        const editor: MockEditor = {
            selection: undefined,
            revealRange: () => undefined,
        };
        return editor;
    },
};

export const ViewColumn = {
    Beside: 2,
};

export function createOutputChannel(name: string): { name: string; show: (_preserveFocus?: boolean) => void; appendLine: (_message: string) => void } {
    return {
        name,
        show: () => {
            outputShown = true;
        },
        appendLine: (message: string) => {
            outputLines.push(message);
        },
    };
}

export class Position {
    constructor(
        public readonly line: number,
        public readonly character: number
    ) {}
}

export class Range {
    constructor(
        public readonly start: unknown,
        public readonly end: unknown
    ) {}
}

export class Selection {
    constructor(
        public readonly start: unknown,
        public readonly end: unknown
    ) {}
}

export class MarkdownString {
    public baseUri: unknown;
    public isTrusted: unknown;
    public supportHtml = false;
    public supportThemeIcons = false;

    constructor(public readonly value: string) {}
}

export class Hover {
    constructor(
        public readonly contents: unknown[],
        public readonly range?: unknown
    ) {}
}

export const TextEditorRevealType = {
    InCenterIfOutsideViewport: 0,
};

export class WorkspaceEdit {
    createFile(_uri: unknown, _options?: unknown): void {
        createdFiles.push({ uri: _uri, options: _options });
    }
    insert(_uri: unknown, _position: unknown, _text: string): void {
        createdEdits.push({ uri: _uri, position: _position, text: _text });
    }
}

export const workspace = {
    workspaceFolders: undefined as Array<{ uri: { fsPath: string } }> | undefined,
    getConfiguration: (_section: string) => ({
        get: <T,>(key: string, defaultValue: T) => configReader<T>(key, defaultValue),
    }),
    createFileSystemWatcher: (_pattern: string) => ({
        dispose: () => undefined,
    }),
    onDidChangeConfiguration: (handler: ConfigChangeHandler) => {
        onDidChangeConfigurationHandler = handler;
        return {
            dispose: () => {
                onDidChangeConfigurationHandler = undefined;
            },
        };
    },
    applyEdit: async (_edit: unknown) => {
        appliedEdits.push(_edit);
        return true;
    },
    openTextDocument: async (_uri: unknown): Promise<MockDocument> => {
        openedDocuments.push(_uri);
        return {
            uri: _uri,
            lineCount: 1,
            lineAt: () => ({ text: documentText }),
        };
    },
};

export const env = {
    clipboard: {
        writeText: async (value: string) => {
            clipboardText = value;
        },
    },
};

export function setConfigurationReader(reader: ConfigReader): void {
    configReader = reader;
}

export function setNextWarningChoice(choice: string | undefined): void {
    nextWarningChoice = choice;
}

export function setNextInfoChoice(choice: string | undefined): void {
    nextInfoChoice = choice;
}

export function setNextSaveDialogUri(uri: unknown | null): void {
    nextSaveDialogUri = uri;
}

export function setDocumentText(value: string): void {
    documentText = value;
}

export function getClipboardText(): string {
    return clipboardText;
}

export function getDocumentInteractions(): {
    openedDocuments: unknown[];
    shownDocuments: Array<{ document: unknown; options: unknown }>;
    appliedEdits: unknown[];
    createdEdits: Array<{ uri: unknown; position: unknown; text: string }>;
    createdFiles: Array<{ uri: unknown; options: unknown }>;
} {
    return {
        openedDocuments: [...openedDocuments],
        shownDocuments: [...shownDocuments],
        appliedEdits: [...appliedEdits],
        createdEdits: [...createdEdits],
        createdFiles: [...createdFiles],
    };
}

export function getExecutedCommands(): Array<{ id: string; args: unknown[] }> {
    return [...executedCommands];
}

export function wasOutputShown(): boolean {
    return outputShown;
}

export function getOutputLines(): string[] {
    return [...outputLines];
}

export async function fireDidChangeConfiguration(section = 'efquerylens'): Promise<void> {
    if (!onDidChangeConfigurationHandler) {
        return;
    }

    await onDidChangeConfigurationHandler({
        affectsConfiguration: (value: string) => value === section,
    });
}

export function getWindowMessages(): { warnings: string[]; infos: string[]; errors: string[] } {
    return {
        warnings: [...warningMessages],
        infos: [...infoMessages],
        errors: [...errorMessages],
    };
}

export function resetVscodeMocks(): void {
    commandHandlers.clear();
    executedCommands.length = 0;
    warningMessages.length = 0;
    infoMessages.length = 0;
    errorMessages.length = 0;
    nextWarningChoice = undefined;
    nextInfoChoice = undefined;
    nextSaveDialogUri = null;
    clipboardText = '';
    documentText = 'var query = context.Entities.Where(e => e.Id > 0);';
    openedDocuments.length = 0;
    shownDocuments.length = 0;
    appliedEdits.length = 0;
    createdEdits.length = 0;
    createdFiles.length = 0;
    outputShown = false;
    outputLines.length = 0;
    onDidChangeConfigurationHandler = undefined;
    workspace.workspaceFolders = undefined;
}

export function resetConfigurationReader(): void {
    configReader = <T,>(_: string, defaultValue: T) => defaultValue;
}
