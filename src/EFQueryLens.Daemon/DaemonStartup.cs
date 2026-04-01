using System.Net;
using System.Text.Json;
using EFQueryLens.Core.Contracts;
using EFQueryLens.Core.Engine;
using EFQueryLens.Core.Scripting.Evaluation;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Memory;

namespace EFQueryLens.Daemon;

internal static class DaemonStartup
{
    internal static void ConfigureLogging(WebApplicationBuilder builder)
    {
        builder.Logging.ClearProviders()
            .AddConsole()
            .SetMinimumLevel(LogLevel.Critical);
    }

    internal static void ConfigureKestrel(WebApplicationBuilder builder, int listenPort)
    {
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, listenPort, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });
    }

    internal static void AddDaemonServices(WebApplicationBuilder builder, JsonSerializerOptions jsonOptions)
    {
        builder.Services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.PropertyNamingPolicy = jsonOptions.PropertyNamingPolicy;
            opts.SerializerOptions.PropertyNameCaseInsensitive = jsonOptions.PropertyNameCaseInsensitive;
            foreach (var converter in jsonOptions.Converters)
                opts.SerializerOptions.Converters.Add(converter);
        });

        builder.Services.AddSingleton<INamespaceTypeIndexCache>(
            _ => new NamespaceTypeIndexCache(maxEntries: 64));
        builder.Services.AddSingleton<IQueryLensEngine>(sp =>
            new QueryLensEngine(sp.GetRequiredService<INamespaceTypeIndexCache>()));
        builder.Services.AddMemoryCache();
        builder.Services.AddSingleton(jsonOptions);
        builder.Services.AddSingleton<DaemonRuntime>(sp =>
            new DaemonRuntime(sp.GetRequiredService<IMemoryCache>()));
    }

    internal static int ResolveBoundPort(WebApplication app, int? requestedPort)
    {
        if (requestedPort is > 0)
            return requestedPort.Value;

        var server = app.Services.GetRequiredService<IServer>();
        var addressesFeature = server.Features.Get<IServerAddressesFeature>();
        var addresses = addressesFeature?.Addresses ?? [];

        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
                continue;

            if (uri.Port > 0)
                return uri.Port;
        }

        throw new InvalidOperationException("Kestrel started but no bound HTTP port was discovered.");
    }
}

