namespace KamenRaider.Controller.Data;

public sealed record AdminVncTarget(string SessionId,string MachineId,string AdminUserId,System.Net.IPAddress Address,int Port,string AllowedOrigin);
