namespace KamenRaider.Agent;
public sealed class AgentOptions
{
    public string ControllerUrl { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public int MoonlightPort { get; set; }
    public int SunshinePort { get; set; }
    public string NetworkAdapterId { get; set; } = "";
    public bool IsValid() => Uri.TryCreate(ControllerUrl, UriKind.Absolute, out var url) && url.Scheme == "https"
        && Path.IsPathFullyQualified(DataDirectory) && MoonlightPort is > 0 and <= 65535
        && SunshinePort is > 0 and <= 65535 && !string.IsNullOrWhiteSpace(NetworkAdapterId);
}
public static class AgentHost
{
    public static IHost Build(string[] args, Action<HostApplicationBuilder>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(args);
        configure?.Invoke(builder);
        builder.Services.AddWindowsService(options => options.ServiceName = "KamenRaider Agent");
        builder.Services.AddOptions<AgentOptions>().Bind(builder.Configuration)
            .Validate(o => o.IsValid(), "HTTPS ControllerUrl, absolute DataDirectory, valid MoonlightPort/SunshinePort and NetworkAdapterId are required.").ValidateOnStart();
        builder.Services.AddHostedService<KamenRaider.Agent.Enrollment.EnrollmentWorker>();
        builder.Services.AddHostedService<KamenRaider.Agent.Sync.SyncWorker>();
        return builder.Build();
    }
}
public class Program { public static void Main(string[] args) => AgentHost.Build(args).Run(); }

