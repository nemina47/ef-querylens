export type QueryLensSqlDialect = 'auto' | 'sql' | 'mysql' | 'transactsql';

export type QueryLensStructuredHoverStatement = {
    Sql: string | null;
    SplitLabel: string | null;
};

export type QueryLensStructuredHoverResponse = {
    Success: boolean;
    ErrorMessage: string | null;
    Statements: QueryLensStructuredHoverStatement[] | null;
    CommandCount: number;
    SourceExpression: string | null;
    DbContextType: string | null;
    ProviderName: string | null;
    SourceFile: string | null;
    SourceLine: number;
    Warnings: string[] | null;
    EnrichedSql: string | null;
    Mode: string | null;
    Status: number;
    StatusMessage: string | null;
    AvgTranslationMs: number;
    LastTranslationMs: number;
};

export type FactoryGenerationResponse = {
    success: boolean;
    message?: string | null;
    content?: string | null;
    suggestedFileName?: string | null;
    dbContextTypeFullName?: string | null;
};

export type QueryLensSettings = {
    maxCodeLensPerDocument: number;
    codeLensDebounceMs: number;
    codeLensUseModelFilter: boolean;
    formatSqlOnShow: boolean;
    sqlDialect: QueryLensSqlDialect;
    debugLogsEnabled: boolean;
};
