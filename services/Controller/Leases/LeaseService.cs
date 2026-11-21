using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
using KamenRaider.Controller.Network;
using Microsoft.Extensions.Options;

namespace KamenRaider.Controller.Leases;

public sealed class LeaseService(ControllerDatabase database, IWakeSender wakeSender, IOptions<ControllerOptions> options)
{
    public Task<LeaseDto> CreateLeaseAsync(string userId,string machineId,int durationSeconds,string idempotencyKey,CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(database.CreateLease(userId,machineId,durationSeconds,idempotencyKey));
    }
    public Task<LeaseDto> ExtendLeaseAsync(string rentalId,int additionalSeconds,long expectedRevision,string idempotencyKey,CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(database.ExtendLease(rentalId,additionalSeconds,expectedRevision,idempotencyKey));
    }
    public Task<LeaseDto?> GetLeaseAsync(string rentalId,CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(database.GetLease(rentalId));
    }
    public Task<LeaseDto[]> GetUserLeasesAsync(string userId,CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(database.GetUserLeases(userId));
    }
    public Task<LeaseDto[]> GetAllLeasesAsync(int take,CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(database.GetAllLeases(take));
    }

    /// <summary>
    /// Sends Wake-on-LAN for a prepared lease. The renter must press Power explicitly; no packet is
    /// sent when the lease is created. The first successful request is idempotent, while a repeated
    /// press can resend the packet when the lease is still waiting for the boot.
    /// </summary>
    public async Task<LeaseDto> RequestPowerAsync(string rentalId,string userId,string requestId,CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        var target=database.GetLeasePowerTarget(rentalId,userId)??throw new KeyNotFoundException("Prepared lease was not found");
        if(target.Lease.WakeRequestedAtUtc is not null) return target.Lease;
        if(!target.MachineOnline) {
            var local=options.Value.WakeInterfaceAddress;
            var broadcast=options.Value.WakeBroadcastAddress;
            if(string.IsNullOrWhiteSpace(local)||string.IsNullOrWhiteSpace(broadcast))
                throw new InvalidOperationException("Wake-on-LAN is not configured on the Controller");
            Exception? last=null;
            for(var attempt=0;attempt<3;attempt++) {
                try { await wakeSender.SendAsync(target.MacAddress,broadcast,ct); last=null; }
                catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                catch(Exception ex) when(ex is ArgumentException or FormatException or System.Net.Sockets.SocketException) { last=ex; }
                if(attempt<2&&last is null) {
                    try { await Task.Delay(500,ct); }
                    catch(OperationCanceledException) when(ct.IsCancellationRequested) { throw; }
                }
            }
            if(last is not null) throw new InvalidOperationException("Wake-on-LAN delivery failed",last);
        }
        return database.MarkLeasePowerRequested(rentalId,userId,requestId);
    }

    public Task<LeaseDto> CancelUserLeaseAsync(string rentalId,string userId,long expectedRevision,string idempotencyKey,CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(database.CancelUserLease(rentalId,userId,expectedRevision,idempotencyKey));
    }
}
