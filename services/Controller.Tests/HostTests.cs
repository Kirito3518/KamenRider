using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using KamenRaider.Controller;

namespace Controller.Tests;
public class HostTests
{
    [Fact]
    public async Task InternalHealth_RequiresCredential()
    {
        await using var app = ControllerHost.Build([], builder => {
            builder.Environment.EnvironmentName = "Development";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["InternalUrl"] = "http://127.0.0.1:5101", ["AgentUrl"] = "https://127.0.0.1:5102",
                ["DatabasePath"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".db"), ["ServiceCredential"] = "test-credential" });
            builder.WebHost.UseTestServer();
            builder.Services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, LoopbackTestFilter>();
        });
        await app.StartAsync();
        var server = app.GetTestServer();
        var client = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/internal/health")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Service-Credential", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/internal/health")).StatusCode);
        client.DefaultRequestHeaders.Remove("X-Service-Credential");
        client.DefaultRequestHeaders.Add("X-Service-Credential", "test-credential");
        var response = await client.GetAsync("/internal/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"protocolVersion\":1,\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Add("Test-Local-Port", "5102");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/internal/health")).StatusCode);
        client.DefaultRequestHeaders.Remove("Test-Local-Port");
        client.DefaultRequestHeaders.Add("Test-Remote-IP", "192.0.2.1");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/internal/health")).StatusCode);
    }
    [Fact]
    public async Task Production_RejectsMissingConfiguration()
    {
        await using var app = ControllerHost.Build([], b => b.Environment.EnvironmentName = "Production");
        await Assert.ThrowsAnyAsync<Exception>(() => app.StartAsync());
    }
    [Theory]
    [InlineData("http://127.0.0.1:0", "https://127.0.0.1:5102")]
    [InlineData("http://127.0.0.1:5101", "https://127.0.0.1:0")]
    public void Configuration_RejectsDynamicPorts(string internalUrl, string agentUrl)
    {
        Assert.False(new ControllerOptions { InternalUrl = internalUrl, AgentUrl = agentUrl, DatabasePath = Path.GetFullPath("unused.db"), ServiceCredential = "test" }.IsValid());
    }
    [Fact]
    public async Task Build_DoesNotCreateProductionDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid()+".db");
        using var app = ControllerHost.Build([], b => b.Configuration["DatabasePath"] = path);
        Assert.False(File.Exists(path));
        var project = FindControllerProject();
        var artifactsPath = Path.Combine(Path.GetTempPath(), "kr-controller-build-" + Guid.NewGuid().ToString("N"));
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[] { "build", project, "--artifacts-path", artifactsPath, "--verbosity", "quiet", "-p:DatabasePath=" + path })
                startInfo.ArgumentList.Add(argument);
            using var build = ContainedProcess.Start(startInfo);
            var result = await ChildProcess.RunAsync(build, TimeSpan.FromSeconds(60));
            Assert.True(result.ExitCode == 0, result.Output + result.Error);
            Assert.False(File.Exists(path));
        }
        finally
        {
            var fullArtifactsPath = Path.GetFullPath(artifactsPath);
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!fullArtifactsPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullArtifactsPath).StartsWith("kr-controller-build-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing unsafe temporary build cleanup.");
            if (Directory.Exists(fullArtifactsPath)) Directory.Delete(fullArtifactsPath, recursive: true);
        }
    }

    private static string FindControllerProject()
    {
        var value = typeof(HostTests).Assembly
            .GetCustomAttributes(inherit: false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => attribute.Key == "ControllerProjectPath")?.Value;
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
            throw new FileNotFoundException("Unable to locate the Controller project using the build-time test metadata.");
        return Path.GetFullPath(value);
    }
}
public sealed class LoopbackTestFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
{
    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app => {
        app.Use((context, continuation) => { context.Connection.LocalPort = int.TryParse(context.Request.Headers["Test-Local-Port"], out var port) ? port : 5101; context.Connection.RemoteIpAddress = IPAddress.TryParse(context.Request.Headers["Test-Remote-IP"], out var remote) ? remote : IPAddress.Loopback; return continuation(context); });
        next(app);
    };
}
