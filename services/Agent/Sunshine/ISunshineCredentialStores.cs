namespace KamenRaider.Agent.Sunshine;

/// <summary>Persistence for the fleet Sunshine credential revision this Agent has applied.</summary>
public interface ISunshineCredentialRevisionStore
{
    /// <summary>Returns the applied revision; 0 means no fleet credential has been applied yet.</summary>
    long Read();

    /// <summary>Records the applied revision after its credential was stored.</summary>
    void Save(long revision);
}

/// <summary>Protected storage sink for a delivered fleet Sunshine management credential.</summary>
public interface ISunshineManagementCredentialSink
{
    void Save(SunshineManagementCredential credential, bool replace);
}
