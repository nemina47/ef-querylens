namespace EFQueryLens.Core.Daemon;

/// <summary>
/// Well-known JSON-RPC method names used between LSP/CLI clients and the QueryLens daemon.
/// </summary>
public static class DaemonMethods
{
    public const string Translate = "ql/translate";
    public const string TranslateQueued = "ql/translateQueued";
    public const string InspectModel = "ql/inspectModel";
    public const string GetState = "ql/getState";
    public const string Ping = "ql/ping";
    public const string Shutdown = "ql/shutdown";

    public const string StateChanged = "ql/stateChanged";
    public const string ConfigReloaded = "ql/configReloaded";
    public const string AssemblyChanged = "ql/assemblyChanged";
}

public enum DaemonWarmState
{
    Cold = 0,
    Warming = 1,
    Ready = 2,
}

public sealed record DaemonTranslateRequest
{
    public required string ContextName { get; init; }
    public required TranslationRequest Request { get; init; }
}

public sealed record DaemonTranslateResponse
{
    public required QueryTranslationResult Result { get; init; }
}

public sealed record DaemonQueuedTranslateRequest
{
    public required string ContextName { get; init; }
    public required string SemanticKey { get; init; }
    public required TranslationRequest Request { get; init; }
}

public sealed record DaemonQueuedTranslateResponse
{
    public required QueryTranslationStatus Status { get; init; }
    public string? JobId { get; init; }
    public required double AverageTranslationMs { get; init; }
    public QueryTranslationResult? Result { get; init; }
}

public sealed record DaemonInspectRequest
{
    public required string ContextName { get; init; }
    public required ModelInspectionRequest Request { get; init; }
}

public sealed record DaemonInspectResponse
{
    public required ModelSnapshot Result { get; init; }
}

public sealed record DaemonStateRequest;

public sealed record DaemonStateResponse
{
    public IReadOnlyList<DaemonContextState> Contexts { get; init; } = [];
}

public sealed record DaemonContextState
{
    public required string ContextName { get; init; }
    public required DaemonWarmState State { get; init; }
}

public sealed record DaemonPingRequest;

public sealed record DaemonPingResponse
{
    public required string Version { get; init; }
    public required TimeSpan Uptime { get; init; }
}

public sealed record DaemonShutdownRequest;

public sealed record DaemonShutdownResponse;

public sealed record DaemonStateChangedNotification
{
    public required string ContextName { get; init; }
    public required DaemonWarmState State { get; init; }
}

public sealed record DaemonConfigReloadedNotification
{
    public IReadOnlyList<string> ContextNames { get; init; } = [];
}

public sealed record DaemonAssemblyChangedNotification
{
    public required string ContextName { get; init; }
    public required string AssemblyPath { get; init; }
}
