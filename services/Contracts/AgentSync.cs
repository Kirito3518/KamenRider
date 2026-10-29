namespace KamenRaider.Contracts;
public record SyncRequest(string BootId,string LocalIp,string MacAddress,long AppliedConfigRevision,string Readiness,CommandResult[] CommandResults);
public record CommandResult(string CommandId,string Status);
public record ConfigurationDto(long Revision,int MoonlightPort,int SunshinePort);
public record AgentCommand(string CommandId,string Type);
public record SyncResponse(DateTimeOffset ServerTimeUtc,ConfigurationDto Configuration,AgentCommand[] Commands);
