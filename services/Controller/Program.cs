using Microsoft.Extensions.DependencyInjection.Extensions;
using KamenRaider.Controller.Agents;
using KamenRaider.Controller.Leases;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using KamenRaider.Controller.Security;
using KamenRaider.Controller.RemoteDesktop;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using KamenRaider.Contracts;
namespace KamenRaider.Controller;

public sealed class ControllerOptions
{
    public string WakeInterfaceAddress { get; set; } = "";
    public string WakeBroadcastAddress { get; set; } = "";
    public string PublicIpEndpoint { get; set; } = "";
    public string InternalUrl { get; set; } = "";
    public string AgentUrl { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public string ServiceCredential { get; set; } = "";
    public bool AgentReleaseRemediationEnabled { get; set; }
    public bool IsValid() => Uri.TryCreate(InternalUrl, UriKind.Absolute, out var local)
        && local.Scheme == "http" && IPAddress.TryParse(local.Host, out var ip) && IPAddress.IsLoopback(ip)
        && Uri.TryCreate(AgentUrl, UriKind.Absolute, out var agent) && agent.Scheme == "https"
        && local.Port > 0 && agent.Port > 0 && local.Port != agent.Port && Path.IsPathFullyQualified(DatabasePath)
        && !string.IsNullOrWhiteSpace(ServiceCredential);
}
public static class ControllerHost
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddWindowsService(options => options.ServiceName = "KamenRaider Controller");
        builder.Services.AddOptions<ControllerOptions>().Bind(builder.Configuration)
            .Validate(o => o.IsValid(), "InternalUrl (loopback HTTP), AgentUrl (separate HTTPS port), absolute DatabasePath and ServiceCredential are required.").ValidateOnStart();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton<IExternalRecoveryHistory, UnavailableExternalRecoveryHistory>();
        var settings = builder.Configuration.Get<ControllerOptions>() ?? new();
        // Invalid configuration is rejected by startup validation before any listener starts.
        if (settings.IsValid())
        {
            var local = new Uri(settings.InternalUrl);
            var agent = new Uri(settings.AgentUrl);
            var inMemoryTestServer = builder.Services.Any(descriptor =>
                descriptor.ServiceType.FullName == "Microsoft.AspNetCore.Hosting.Server.IServer"
                && descriptor.ImplementationType?.FullName == "Microsoft.AspNetCore.TestHost.TestServer");
            if (!inMemoryTestServer)
            {
                builder.Services.AddSingleton(sp => new ControllerDatabaseActivityLock(settings.DatabasePath, requireRestoreEpoch: true));
                builder.Services.AddHostedService(sp => sp.GetRequiredService<ControllerDatabaseActivityLock>());
            }
            builder.WebHost.ConfigureKestrel(server => {
                server.Listen(IPAddress.Parse(local.Host), local.Port);
                if (agent.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                    || IPAddress.TryParse(agent.Host.Trim('[', ']'), out var agentAddress) && IPAddress.IsLoopback(agentAddress))
                    server.ListenLocalhost(agent.Port, endpoint => endpoint.UseHttps());
                else
                    server.Listen(IPAddress.Any, agent.Port, endpoint => endpoint.UseHttps());
            });
        }
        builder.Services.AddSingleton<ControllerDatabase>(sp => new ControllerDatabase(
            sp.GetRequiredService<IOptions<ControllerOptions>>().Value.DatabasePath,
            clock: sp.GetRequiredService<TimeProvider>(),
            agentReleaseRemediationEnabled: sp.GetRequiredService<IOptions<ControllerOptions>>().Value.AgentReleaseRemediationEnabled));
        builder.Services.AddSingleton<AgentReleaseStore>(sp => {
            var databasePath = sp.GetRequiredService<IOptions<ControllerOptions>>().Value.DatabasePath;
            var databaseDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
            return new AgentReleaseStore(Path.Combine(databaseDirectory, "agent-releases"));
        });
        builder.Services.TryAddSingleton<IAdminVncTargetProbe, AdminVncTargetProbe>();
        builder.Services.AddSingleton<AgentAuthentication>();
        builder.Services.AddSingleton<LeaseService>();
        builder.Services.TryAddSingleton<KamenRaider.Controller.Network.IWakeSender>(_ => new KamenRaider.Controller.Network.WakeService(settings.WakeInterfaceAddress));
        if (Uri.TryCreate(settings.PublicIpEndpoint, UriKind.Absolute, out var publicIp) && publicIp.Scheme == "https") {
            builder.Services.AddSingleton(_ => new KamenRaider.Controller.Network.PublicIpJob(new HttpClient(),publicIp));
            builder.Services.AddHostedService(sp=>sp.GetRequiredService<KamenRaider.Controller.Network.PublicIpJob>());
        }
        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (context, next) => {
            var options = context.RequestServices.GetRequiredService<IOptions<ControllerOptions>>().Value;
            var internalPort = new Uri(options.InternalUrl).Port;
            var isInternalListener = context.Connection.LocalPort == internalPort;
            if (context.Request.Path.StartsWithSegments("/internal"))
            {
                if (!isInternalListener || context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
                { context.Response.StatusCode = 404; return; }
                var credential = context.Request.Headers["X-Service-Credential"].ToString();
                if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(credential)), SHA256.HashData(Encoding.UTF8.GetBytes(options.ServiceCredential))))
                { context.Response.StatusCode = 401; return; }
            }
            else if (isInternalListener || !context.Request.IsHttps)
            { context.Response.StatusCode = 404; return; }
            await next(context);
        });
        app.MapGet("/internal/health", () => new HealthResponse(Protocol.Version, "ok"));
        app.MapMachines();
        app.MapSync();
        app.MapLeases();
        app.MapPairing();
        app.MapAdminVnc();
        app.MapAgentReleases();
        return app;
    }
}
public class Program
{
    public static int Main(string[] args)
    {
        if (ControllerMaintenanceCommand.TryRun(args, out var exitCode)) return exitCode;
        ControllerHost.Build(args).Run();
        return 0;
    }
}

public static class ControllerMaintenanceCommand
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] is not ("--backup-database" or "--restore-database" or "--bootstrap-restore-epoch")) return false;

        var invalidBackupArguments = args[0] == "--backup-database" && args.Length != 3;
        var invalidRestoreArguments = args[0] == "--restore-database" && (args.Length != 4 || args[3] != "--controller-stopped");
        var invalidBootstrapArguments = args[0] == "--bootstrap-restore-epoch" && (args.Length != 3 || args[2] != "--controller-stopped");
        if (invalidBackupArguments || invalidRestoreArguments || invalidBootstrapArguments)
        {
            Console.Error.WriteLine("Usage: Controller.exe --backup-database <database> <backup> | --restore-database <backup> <database> --controller-stopped | --bootstrap-restore-epoch <database> --controller-stopped");
            exitCode = 2;
            return true;
        }

        try
        {
            if (args[0] == "--backup-database")
            {
                var destination = ControllerDatabaseMaintenance.Backup(args[1], args[2]);
                Console.WriteLine($"Verified database backup: {destination}");
            }
            else if (args[0] == "--restore-database")
            {
                var recovery = ControllerDatabaseMaintenance.Restore(args[1], args[2]);
                Console.WriteLine(recovery is null
                    ? "Database restore completed; there was no prior database to preserve."
                    : $"Database restore completed. Prior database preserved at: {recovery}");
            }
            else
            {
                var epoch = ControllerDatabaseMaintenance.BootstrapRestoreEpochForExistingDatabase(args[1]);
                Console.WriteLine($"Restore quarantine is active; restore epoch provisioned: {epoch}");
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Controller database maintenance failed: {exception.Message}");
            exitCode = 1;
        }

        return true;
    }
}
