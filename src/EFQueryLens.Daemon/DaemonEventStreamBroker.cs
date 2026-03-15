using System.Collections.Concurrent;
using System.Threading.Channels;
using EFQueryLens.Core.Grpc;

namespace EFQueryLens.Daemon;

internal sealed class DaemonEventStreamBroker
{
    private readonly ConcurrentDictionary<long, Channel<DaemonEvent>> _subscribers = new();
    private long _nextSubscriberId;

    public Subscription Subscribe(CancellationToken cancellationToken)
    {
        var subscriberId = Interlocked.Increment(ref _nextSubscriberId);
        var channel = Channel.CreateUnbounded<DaemonEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        _subscribers[subscriberId] = channel;

        var registration = cancellationToken.Register(() => Unsubscribe(subscriberId));
        return new Subscription(channel.Reader, () =>
        {
            registration.Dispose();
            Unsubscribe(subscriberId);
        });
    }

    public void PublishStateChanged(string contextName, DaemonWarmState state)
    {
        if (string.IsNullOrWhiteSpace(contextName))
        {
            return;
        }

        Publish(new DaemonEvent
        {
            StateChanged = new StateChangedEvent
            {
                ContextName = contextName,
                State = state,
            },
        });
    }

    public void PublishConfigReloaded(IReadOnlyCollection<string> contextNames)
    {
        var eventPayload = new DaemonEvent
        {
            ConfigReloaded = new ConfigReloadedEvent(),
        };
        eventPayload.ConfigReloaded.ContextNames.AddRange(contextNames);
        Publish(eventPayload);
    }

    public void PublishAssemblyChanged(string contextName, string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(contextName) || string.IsNullOrWhiteSpace(assemblyPath))
        {
            return;
        }

        Publish(new DaemonEvent
        {
            AssemblyChanged = new AssemblyChangedEvent
            {
                ContextName = contextName,
                AssemblyPath = assemblyPath,
            },
        });
    }

    private void Publish(DaemonEvent daemonEvent)
    {
        foreach (var subscriber in _subscribers)
        {
            if (!subscriber.Value.Writer.TryWrite(daemonEvent))
            {
                Unsubscribe(subscriber.Key);
            }
        }
    }

    private void Unsubscribe(long subscriberId)
    {
        if (_subscribers.TryRemove(subscriberId, out var channel))
        {
            channel.Writer.TryComplete();
        }
    }

    internal readonly struct Subscription : IDisposable
    {
        private readonly Action _dispose;

        public Subscription(ChannelReader<DaemonEvent> reader, Action dispose)
        {
            Reader = reader;
            _dispose = dispose;
        }

        public ChannelReader<DaemonEvent> Reader { get; }

        public void Dispose() => _dispose();
    }
}
