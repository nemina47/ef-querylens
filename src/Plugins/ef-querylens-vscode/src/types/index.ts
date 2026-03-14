export type QueryLensSqlDialect = 'auto' | 'sql' | 'mysql' | 'transactsql';

export type QueryLensHoverMetadata = {
    SourceExpression: string;
    ExecutedExpression: string;
    Mode: string;
    ModeDescription: string;
    Warnings: string[];
    SourceFile: string;
    SourceLine: number;
    DbContextType: string;
    ProviderName: string;
    CreationStrategy: string;
};

export type QueryLensSettings = {
    maxCodeLensPerDocument: number;
    codeLensDebounceMs: number;
    codeLensUseModelFilter: boolean;
    formatSqlOnShow: boolean;
    sqlDialect: QueryLensSqlDialect;
    debugLogsEnabled: boolean;
};

export type StagedRuntime = {
    lspDllPath: string;
    daemonDllPath: string;
    daemonExePath: string;
    stagingRoot: string;
};
