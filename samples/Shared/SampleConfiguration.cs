using System.Reflection;
using ElBruno.AI.Jev;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jev.Samples;

internal static class SampleConfiguration
{
    internal static IHost CreateHost(string[] args)
    {
        bool offline = args.Contains("--offline", StringComparer.Ordinal);
        string[] configurationArgs = args.Where(argument => argument != "--offline").ToArray();
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(configurationArgs);
        builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true)
            .AddEnvironmentVariables().AddCommandLine(configurationArgs);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        IHttpClientBuilder transport = builder.Services.AddJev(options =>
        {
            options.ApiKey = offline ? "offline-fixture" : builder.Configuration["Jev:ApiKey"] ?? "";
            options.DefaultModel = builder.Configuration["Jev:DefaultModel"] ?? JevModels.Version1_13_0;
            options.MaxRetries = 0;
        });
        if (offline)
        {
            transport.ConfigurePrimaryHttpMessageHandler(() => new OfflineJevHandler());
            Console.WriteLine("OFFLINE DEMO: synthetic fixtures, no network or AI evaluation.");
        }
        else
        {
            Console.WriteLine("LIVE Jev calls: synthetic sample input, retries disabled. Calls may incur charges.");
        }

        return builder.Build();
    }

    internal static async Task<int> RunAsync(string[] args, Func<IJevDecisionClient, CancellationToken, Task> run)
    {
        if (args.Contains("--help", StringComparer.Ordinal))
        {
            Console.WriteLine("Run with --offline for synthetic fixtures, or configure Jev:ApiKey in user-secrets for live calls.");
            return 0;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            using IHost host = CreateHost(args);
            await host.StartAsync(cancellation.Token);
            await run(host.Services.GetRequiredService<IJevDecisionClient>(), cancellation.Token);
            await host.StopAsync(CancellationToken.None);
            return 0;
        }
        catch (OptionsValidationException)
        {
            Console.Error.WriteLine("Configure the official TypeSafe key using dotnet user-secrets set \"Jev:ApiKey\" \"<key>\" --project <sample>, or explicitly pass --offline.");
            return 1;
        }
        catch (JevException exception)
        {
            Console.Error.WriteLine($"{exception.GetType().Name}: {exception.Message}");
            return 1;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled. Remote execution or billing may already have occurred.");
            return 130;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
