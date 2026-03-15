using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;
using EFQueryLens.Core;
using EFQueryLens.Core.Daemon;
using StreamJsonRpc;

namespace EFQueryLens.Daemon;

internal sealed class DaemonHost(
    string pipeName,
    IQueryLensEngine engine,
    SqlTranslationQueue queue,
    TranslationMetrics metrics)
{
    private readonly DateTime _startedUtc = DateTime.UtcNow;
    private readonly bool _debugEnabled = ReadBoolEnvironmentVariable("QUERYLENS_DEBUG", fallback: false);
    private readonly ConcurrentDictionary<string, DaemonWarmState> _contextStates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<int, Task> _clientSessions = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private int _nextSessionId;

    public async Task RunAsync(CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        var runToken = linkedCts.Token;

        while (!runToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
            }
            catch (Exception ex)
            {
                LogError($"daemon-pipe-create-failed type={ex.GetType().Name} message={ex.Message}");
                try
                {
                    await Task.Delay(250, runToken);
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            try
            {
                await pipe.WaitForConnectionAsync(runToken);
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync();
                break;
            }
            catch (Exception ex)
            {
                await pipe.DisposeAsync();
                LogDebug($"daemon-wait-for-connection-failed type={ex.GetType().Name} message={ex.Message}");
                continue;
            }

            var sessionId = Interlocked.Increment(ref _nextSessionId);
            var service = new QueryLensDaemonService(engine, queue, metrics, _shutdownCts, _contextStates, _startedUtc);
            var rpc = new JsonRpc(BuildMessageHandler(pipe), service);
            LogDebug("daemon-client-connected");
            _clientSessions[sessionId] = RunSessionAsync(sessionId, rpc, pipe);
        }

        var remainingSessions = _clientSessions.Values.ToArray();
        if (remainingSessions.Length > 0)
        {
            try
            {
                await Task.WhenAll(remainingSessions);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogError($"daemon-session-join-failed type={ex.GetType().Name} message={ex.Message}");
            }
        }
    }

    private async Task RunSessionAsync(int sessionId, JsonRpc rpc, NamedPipeServerStream pipe)
    {
        try
        {
            rpc.StartListening();
            await rpc.Completion;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDebug($"daemon-client-session-failed type={ex.GetType().Name} message={ex.Message}");
        }
        finally
        {
            rpc.Dispose();
            await pipe.DisposeAsync();
            _clientSessions.TryRemove(sessionId, out _);
            LogDebug("daemon-client-disconnected");
        }
    }

    internal static IJsonRpcMessageHandler BuildMessageHandler(Stream stream)
    {
        var formatter = new SystemTextJsonFormatter
        {
            JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
        };
        return new LengthHeaderMessageHandler(stream, stream, formatter);
    }

    private void LogDebug(string message)
    {
        if (!_debugEnabled) return;
        Console.Error.WriteLine($"[QL-DAEMON] {message}");
    }

    private static void LogError(string message)
    {
        Console.Error.WriteLine($"[QL-DAEMON] {message}");
    }

    private static bool ReadBoolEnvironmentVariable(string variableName, bool fallback)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (bool.TryParse(raw, out var parsed)) return parsed;
        return raw.Equals("1", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || raw.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}