using System.Text;
using System.Text.Json;
using KamenRaider.AgentPackage;

namespace KamenRaider.AgentMaintenance;

public interface IAgentMaintenanceCommandHost
{
    AgentMaintenanceCommandResult Execute(string command);
}

public sealed class WindowsAgentMaintenanceCommandHost(LocalUpdateEngine engine) : IAgentMaintenanceCommandHost
{
    private static readonly HashSet<string> KnownCommands = new(StringComparer.Ordinal)
    {
        "inspect", "validate-archive", "extract", "start", "bind-source", "apply", "report", "reconcile"
    };

    public AgentMaintenanceCommandResult Execute(string command)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (!KnownCommands.Contains(command))
            throw new InvalidOperationException("The maintenance command is not recognized.");

        if (command is "validate-archive" or "extract")
        {
            var inventory = command == "validate-archive" ? engine.ValidateArchive() : engine.Extract();
            return new("ok", null, inventory.ArchiveHash, inventory.Version, inventory.TargetRuntime,
                inventory.InventoryDigest, inventory.TotalBytes, inventory.Nodes);
        }

        return new("unavailable", "operationNotImplemented", null, null, null, null, null, null);
    }
}

public static class AgentMaintenanceCli
{
    private const int MaximumResultBytes = 2_000_000;
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "inspect", "validate-archive", "extract", "start", "bind-source", "apply", "report", "reconcile"
    };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static int Run(string[] args, IAgentMaintenanceCommandHost host, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (args.Length != 1 || !Commands.Contains(args[0]))
            return WriteResult(new("rejected", "arguments", null, null, null, null, null, null), output, error, 2);

        try
        {
            var result = host.Execute(args[0]);
            return WriteResult(result, output, error, result.Status == "ok" ? 0 : 3);
        }
        catch (PackageValidationException exception)
        {
            return WriteResult(new("rejected", exception.Category, null, null, null, null, null, null), output, error, 4);
        }
        catch (InvalidDataException)
        {
            return WriteResult(new("rejected", "request", null, null, null, null, null, null), output, error, 4);
        }
        catch (InvalidOperationException)
        {
            return WriteResult(new("rejected", "precondition", null, null, null, null, null, null), output, error, 4);
        }
        catch (Exception)
        {
            return WriteResult(new("failed", "localOperation", null, null, null, null, null, null), output, error, 5);
        }
    }

    private static int WriteResult(AgentMaintenanceCommandResult result, TextWriter output, TextWriter error, int exitCode)
    {
        var json = JsonSerializer.Serialize(result, JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumResultBytes)
        {
            json = JsonSerializer.Serialize(new AgentMaintenanceCommandResult(
                "rejected", "resultLimit", null, null, null, null, null, null), JsonOptions);
            exitCode = 4;
        }
        output.WriteLine(json);
        return exitCode;
    }
}
