namespace KamenRaider.Agent.Identity;

/// <summary>
/// Produces the canonical signed-Agent-request header value without exposing private key material.
/// Implemented by <see cref="DeviceIdentity"/>; the interface exists so request builders can be tested
/// without provisioning the on-disk protected identity.
/// </summary>
public interface IAgentRequestSigner
{
    string MachineId { get; }

    string Sign(string method, string path, DateTimeOffset timestamp, string nonce, string body);
}
