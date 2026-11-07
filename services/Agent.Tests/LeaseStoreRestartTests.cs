using KamenRaider.Agent.Leases;

namespace Agent.Tests;

public class LeaseStoreRestartTests
{
    [Fact]
    public async Task PublishedSnapshotIsLoadedByANewStoreInstance()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lease-store-restart-" + Guid.NewGuid().ToString("N"));
        var expected = new LeaseSnapshot("rental-1", 7, new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero), "boot-1", "command-7", 1234);

        try
        {
            await new LeaseStore(directory).SaveAsync(expected, CancellationToken.None);

            var actual = await new LeaseStore(directory).LoadAsync(CancellationToken.None);

            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestartLoadsCommittedSnapshotWhenAnIncompleteTemporaryFileRemains()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lease-store-restart-" + Guid.NewGuid().ToString("N"));
        var expected = new LeaseSnapshot("rental-2", 3, new DateTimeOffset(2026, 11, 7, 13, 0, 0, TimeSpan.Zero), "boot-2", "command-3", 5678);

        try
        {
            await new LeaseStore(directory).SaveAsync(expected, CancellationToken.None);
            var orphanedTemporaryFile = Path.Combine(directory, "interrupted-" + Guid.NewGuid().ToString("N") + ".tmp");
            await File.WriteAllTextAsync(orphanedTemporaryFile, "{\"RentalId\":\"rental-staged\",\"Revision\":");

            var actual = await new LeaseStore(directory).LoadAsync(CancellationToken.None);

            Assert.True(File.Exists(orphanedTemporaryFile));
            Assert.Equal(expected, actual);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PublishedClearTombstoneReturnsNoSnapshotAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lease-store-restart-" + Guid.NewGuid().ToString("N"));

        try
        {
            await new LeaseStore(directory).SaveAsync(
                new LeaseSnapshot("rental-3", 2, new DateTimeOffset(2026, 11, 7, 14, 0, 0, TimeSpan.Zero), "boot-3", "command-2", 9012),
                CancellationToken.None);
            await new LeaseStore(directory).ClearAsync(CancellationToken.None);

            var snapshotPath = Path.Combine(directory, "lease.json");
            var actual = await new LeaseStore(directory).LoadAsync(CancellationToken.None);

            Assert.True(File.Exists(snapshotPath));
            Assert.Null(actual);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RejectedSaveIfKeepsPreviouslyCommittedSnapshotAfterRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lease-store-restart-" + Guid.NewGuid().ToString("N"));
        var committed = new LeaseSnapshot("rental-4", 5, new DateTimeOffset(2026, 11, 7, 15, 0, 0, TimeSpan.Zero), "boot-4", "command-5", 3456);
        var rejected = new LeaseSnapshot("rental-4", 6, new DateTimeOffset(2026, 11, 7, 16, 0, 0, TimeSpan.Zero), "boot-4", "command-6", 6789);

        try
        {
            var store = new LeaseStore(directory);
            await store.SaveAsync(committed, CancellationToken.None);

            var accepted = await store.SaveIfAsync(rejected, _ => false, CancellationToken.None);
            var actual = await new LeaseStore(directory).LoadAsync(CancellationToken.None);

            Assert.False(accepted);
            Assert.Equal(committed, actual);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
