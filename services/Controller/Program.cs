using Microsoft.Extensions.DependencyInjection.Extensions;
using KamenRaider.Controller.Agents;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Machines;
using KamenRaider.Controller.Security;
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
        var settings = builder.Configuration.Get<ControllerOptions>() ?? new();
        // Invalid configuration is rejected by startup validation before any listener starts.
        if (settings.IsValid())
        {
            var local = new Uri(settings.InternalUrl);
            var agent = new Uri(settings.AgentUrl);
            builder.WebHost.ConfigureKestrel(server => {
                server.Listen(IPAddress.Parse(local.Host), local.Port);
                server.Listen(IPAddress.Any, agent.Port, endpoint => endpoint.UseHttps());
            });
        }
        builder.Services.AddSingleton<ControllerDatabase>(sp => new ControllerDatabase(sp.GetRequiredService<IOptions<ControllerOptions>>().Value.DatabasePath));
        builder.Services.AddSingleton<AgentAuthentication>();
        builder.Services.TryAddSingleton<KamenRaider.Controller.Network.IWakeSender>(_ => new KamenRaider.Controller.Network.WakeService(settings.WakeInterfaceAddress));
        if (Uri.TryCreate(settings.PublicIpEndpoint, UriKind.Absolute, out var publicIp) && publicIp.Scheme == "https") {
            builder.Services.AddSingleton(_ => new KamenRaider.Controller.Network.PublicIpJob(new HttpClient(),publicIp));
            builder.Services.AddHostedService(sp=>sp.GetRequiredService<KamenRaider.Controller.Network.PublicIpJob>());
        }
        var app = builder.Build();
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
        return app;
    }
}
public class Program { public static void Main(string[] args) => ControllerHost.Build(args).Run(); }


