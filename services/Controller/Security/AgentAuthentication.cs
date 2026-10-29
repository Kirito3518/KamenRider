using System.Security.Cryptography;
using System.Text;
namespace KamenRaider.Controller.Security;
public sealed class AgentAuthentication
{
    private readonly Dictionary<(string,string),DateTimeOffset> nonces=new();
    public bool Verify(string id,string publicKey,string method,string path,DateTimeOffset timestamp,string nonce,string body,string signature,DateTimeOffset now) {
        if(Math.Abs((now-timestamp).TotalSeconds)>60||string.IsNullOrWhiteSpace(nonce)||nonce.Length>128)return false;
        try {
            using var key=ECDsa.Create();key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey),out _);
            var data=Encoding.UTF8.GetBytes($"{id}\n{method}\n{path}\n{timestamp.ToUnixTimeSeconds()}\n{nonce}\n{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)))}");
            if(!key.VerifyData(data,Convert.FromBase64String(signature),HashAlgorithmName.SHA256))return false;
        } catch(Exception ex) when(ex is CryptographicException or FormatException or ArgumentException){return false;}
        lock(nonces) {
            foreach(var expired in nonces.Where(x=>x.Value<=now).Select(x=>x.Key).ToArray())nonces.Remove(expired);
            return nonces.TryAdd((id,nonce),now.AddSeconds(120));
        }
    }
}
