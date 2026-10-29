using KamenRaider.Contracts;
using KamenRaider.Controller.Data;
namespace KamenRaider.Controller.Leases;
public sealed class LeaseService(ControllerDatabase database) {
    public Task<LeaseDto> CreateTrialLeaseAsync(string userId,string machineId,int durationSeconds,string idempotencyKey,CancellationToken ct) {
        ct.ThrowIfCancellationRequested(); return Task.FromResult(database.CreateTrialLease(userId,machineId,durationSeconds,idempotencyKey));
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
}
