using KamenRaider.Contracts;
using System.Text.Json;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
    public SyncResponse Sync(string id,SyncRequest request) {
        lock(gate) {
            ValidateNetworkMetadata(request.LocalIp,request.MacAddress);
            if(string.IsNullOrWhiteSpace(request.BootId)||request.BootId.Length>128||request.Readiness is not ("ready" or "notReady" or "unknown")||request.CommandResults is null||request.CommandResults.Length>100||request.CommandResults.Any(r=>r is null)||request.AgentVersion is { } version&&(string.IsNullOrWhiteSpace(version)||version.Length>64||version.Any(c=>!char.IsAsciiLetterOrDigit(c)&&c is not ('.' or '-' or '+'))))
                throw new ArgumentException("Invalid heartbeat");
            var diagnostics=ValidateReadinessDiagnostics(request.ReadinessDiagnostics);
            var machine=List().SingleOrDefault(x=>x.MachineId==id)??throw new KeyNotFoundException();var now=clock.GetUtcNow();
            using var tx=connection.BeginTransaction();
            if(request.Readiness=="ready")RegisterBoot(tx,id,request.BootId);
            else RejectRetiredBoot(tx,id,request.BootId);
            if(request.AppliedConfigRevision>=0)ExecuteLease(tx,"INSERT INTO machine_config_acks(machine_id,applied_revision) VALUES($id,$revision) ON CONFLICT(machine_id) DO UPDATE SET applied_revision=$revision",("$id",id),("$revision",request.AppliedConfigRevision));
            ExpireLeases(tx,now.ToUnixTimeMilliseconds());
            ExecuteLease(tx,"INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness,agent_version) VALUES($id,$at,$boot,$ready,$version) ON CONFLICT(machine_id) DO UPDATE SET last_seen=$at,boot_id=$boot,readiness=$ready,agent_version=COALESCE($version,machine_heartbeats.agent_version); UPDATE machines SET local_ip=$ip,mac_address=$mac WHERE machine_id=$id",("$id",id),("$at",now.ToString("O")),("$boot",request.BootId),("$ready",request.Readiness),("$version",request.AgentVersion),("$ip",request.LocalIp),("$mac",request.MacAddress));
            if(diagnostics is null)ExecuteLease(tx,"DELETE FROM machine_readiness_diagnostics WHERE machine_id=$id",("$id",id));
            else ExecuteLease(tx,"INSERT INTO machine_readiness_diagnostics(machine_id,diagnostics_json,updated_at) VALUES($id,$diagnostics,$at) ON CONFLICT(machine_id) DO UPDATE SET diagnostics_json=$diagnostics,updated_at=$at",("$id",id),("$diagnostics",JsonSerializer.Serialize(diagnostics)),("$at",now.ToString("O")));
            // Presence before reconciliation cannot retire a boot, alter command results, or prove a lease.
            if(request.Readiness!="ready") {
                tx.Commit();return new(now,new(machine.ConfigRevision,machine.MoonlightPort,machine.SunshinePort),[],null);
            }
            string? unbound;
            using(var cmd=LeaseCommand(tx,"SELECT id FROM leases WHERE machine_id=$id AND state='preparing' AND boot_id IS NULL",("$id",id))) unbound=cmd.ExecuteScalar() as string;
            if(unbound is not null) PrepareCommand(tx,FindLease(tx,unbound)!,request.BootId);
            ApplyResults(tx,id,request,now.ToUnixTimeMilliseconds());
            ObservePairingOffer(tx,id,request,now.ToUnixTimeMilliseconds());PurgePairingSecrets(tx,now.ToUnixTimeMilliseconds());
            var response=new SyncResponse(now,new(machine.ConfigRevision,machine.MoonlightPort,machine.SunshinePort),PendingCommands(tx,id,request.BootId),ConfirmLease(tx,id,request.BootId,request.Readiness));
            tx.Commit(); return response;
        }
    }

    private static ReadinessDiagnostic[]? ValidateReadinessDiagnostics(ReadinessDiagnostic[]? diagnostics) {
        if(diagnostics is null)return null;
        if(diagnostics.Length>ReadinessDiagnosticIds.Canonical.Count||diagnostics.Any(item=>item is null||!ReadinessDiagnosticIds.Canonical.Contains(item.CheckId,StringComparer.Ordinal)||item.Status is not (ReadinessDiagnosticStatuses.Passed or ReadinessDiagnosticStatuses.Blocked or ReadinessDiagnosticStatuses.Unavailable or ReadinessDiagnosticStatuses.Unknown))||diagnostics.Select(item=>item.CheckId).Distinct(StringComparer.Ordinal).Count()!=diagnostics.Length)
            throw new ArgumentException("Invalid heartbeat");
        return ReadinessDiagnosticIds.Canonical.Where(id=>diagnostics.Any(item=>item.CheckId==id)).Select(id=>diagnostics.Single(item=>item.CheckId==id)).ToArray();
    }
}
