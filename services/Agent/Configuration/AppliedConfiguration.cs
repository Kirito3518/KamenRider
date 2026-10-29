using KamenRaider.Contracts;
using System.Text.Json;
namespace KamenRaider.Agent.Configuration;
public sealed class AppliedConfiguration {
 private readonly string path;
 public ConfigurationDto? Current {get;private set;}
 public AppliedConfiguration(string directory) {path=Path.Combine(directory,"applied-configuration.json");if(File.Exists(path))Current=JsonSerializer.Deserialize<ConfigurationDto>(File.ReadAllText(path));}
 public async Task ApplyAsync(ConfigurationDto configuration,CancellationToken ct) {
 if(configuration.Revision<=0||configuration.MoonlightPort is <1 or >65535||configuration.SunshinePort is <1 or >65535)throw new InvalidDataException("Invalid controller configuration");
 if(Current is not null&&configuration.Revision<=Current.Revision)return;
 var temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
 try {await using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){await JsonSerializer.SerializeAsync(file,configuration,cancellationToken:ct);await file.FlushAsync(ct);file.Flush(true);}File.Move(temporary,path,true);Current=configuration;}finally{if(File.Exists(temporary))File.Delete(temporary);}
 }
}
