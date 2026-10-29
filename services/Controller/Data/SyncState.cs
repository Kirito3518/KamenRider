using KamenRaider.Contracts;
namespace KamenRaider.Controller.Data;
public sealed partial class ControllerDatabase {
    public SyncResponse Sync(string id,SyncRequest request) {
        lock(gate) {
            ValidateNetworkMetadata(request.LocalIp,request.MacAddress);
            if(string.IsNullOrWhiteSpace(request.BootId)||request.BootId.Length>128||request.Readiness is not ("ready" or "notReady" or "unknown")||request.CommandResults is null||request.CommandResults.Length>100||request.CommandResults.Any(r=>r is null))
                throw new ArgumentException("Invalid heartbeat");
            var machine=List().SingleOrDefault(x=>x.MachineId==id)??throw new KeyNotFoundException();var now=clock.GetUtcNow();
            using var tx=connection.BeginTransaction();
            RegisterBoot(tx,id,request.BootId);
            ExpireLeases(tx,now.ToUnixTimeMilliseconds());
            ExecuteLease(tx,"INSERT INTO machine_heartbeats(machine_id,last_seen,boot_id,readiness) VALUES($id,$at,$boot,$ready) ON CONFLICT(machine_id) DO UPDATE SET last_seen=$at,boot_id=$boot,readiness=$ready; UPDATE machines SET local_ip=$ip,mac_address=$mac WHERE machine_id=$id",("$id",id),("$at",now.ToString("O")),("$boot",request.BootId),("$ready",request.Readiness),("$ip",request.LocalIp),("$mac",request.MacAddress));
            string? unbound;
            using(var cmd=LeaseCommand(tx,"SELECT id FROM leases WHERE machine_id=$id AND state='preparing' AND boot_id IS NULL",("$id",id))) unbound=cmd.ExecuteScalar() as string;
            if(unbound is not null) PrepareCommand(tx,FindLease(tx,unbound)!,request.BootId);
            ApplyResults(tx,id,request,now.ToUnixTimeMilliseconds());
            var response=new SyncResponse(now,new(machine.ConfigRevision,machine.MoonlightPort,machine.SunshinePort),PendingCommands(tx,id,request.BootId),ConfirmLease(tx,id,request.BootId,request.Readiness));
            tx.Commit(); return response;
        }
    }
}
