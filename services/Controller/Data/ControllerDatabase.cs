using Microsoft.Data.Sqlite;
using KamenRaider.Contracts;
using System.Security.Cryptography;
namespace KamenRaider.Controller.Data;
public record EnrollmentPolicy(int MaxPending = 100, int NewPerMinute = 10);
public sealed class EnrollmentCapacityException : Exception { }
public sealed partial class ControllerDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly object gate=new();
    private readonly EnrollmentPolicy policy;
    private readonly TimeProvider clock;
    public ControllerDatabase(string path, EnrollmentPolicy? policy = null, TimeProvider? clock = null) {
        this.policy = policy ?? new(); this.clock = clock ?? TimeProvider.System;
        if (this.policy.MaxPending <= 0 || this.policy.NewPerMinute <= 0) throw new ArgumentException("Positive enrollment limits required");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connection=new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=path, ForeignKeys=true, Pooling=false}.ToString()); connection.Open();
        using var transaction=connection.BeginTransaction();
        foreach (var name in new[] { "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql", "005-config-acks.sql" }) {
            if(name=="003-pairing.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=3)continue;}
            using var resource=typeof(ControllerDatabase).Assembly.GetManifestResourceStream("KamenRaider.Controller.Data.Migrations."+name)!;
            using var reader=new StreamReader(resource);
            using var cmd=connection.CreateCommand(); cmd.Transaction=transaction; cmd.CommandText=reader.ReadToEnd(); cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    private bool disposed;
    public void Dispose(){lock(gate){if(disposed)return;disposed=true;foreach(var timer in pairingPinTimers.Values)timer.Dispose();pairingPinTimers.Clear();pairingPins.Clear();connection.Dispose();}}
    private SqliteCommand Command(string sql, params (string,object?)[] values) { var cmd=connection.CreateCommand();cmd.CommandText=sql; foreach(var (name,value) in values) cmd.Parameters.AddWithValue(name,value??DBNull.Value); return cmd; }
    public string? PublicKey(string id) { lock(gate) {using var cmd=Command("SELECT public_key FROM machines WHERE machine_id=$id",("$id",id)); return cmd.ExecuteScalar() as string;} }
    public EnrollmentResult Enroll(EnrollmentRequest request) { lock(gate) {
        if(string.IsNullOrWhiteSpace(request.MachineId)||request.MachineId.Length>128) throw new ArgumentException("Invalid machine ID");
        using var key=ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.PublicKey),out var bytes);
        if(bytes!=Convert.FromBase64String(request.PublicKey).Length) throw new ArgumentException("Invalid key");
        Ports(request.MoonlightPort,request.SunshinePort);
        ValidateNetworkMetadata(request.LocalIp, request.MacAddress);
        using var transaction = connection.BeginTransaction();
        // Existing identities bypass admission budgets and never rewrite administrator data.
        using (var lookup = Command("SELECT public_key, approval_status FROM machines WHERE machine_id=$id", ("$id", request.MachineId)))
        {
            lookup.Transaction = transaction;
            using var row = lookup.ExecuteReader();
            if (row.Read()) {
                if (row.GetString(0) != request.PublicKey) throw new UnauthorizedAccessException("Identity conflict");
                return new(request.MachineId, row.GetString(1));
            }
        }
        // The immediate transaction serializes capacity checks and admission across connections.
        var cutoff = clock.GetUtcNow().AddMinutes(-1).ToUnixTimeMilliseconds();
        using (var cleanup = Command("DELETE FROM enrollment_admissions WHERE admitted_at <= $cutoff", ("$cutoff", cutoff))) {
            cleanup.Transaction = transaction; cleanup.ExecuteNonQuery();
        }
        using (var capacity = Command("SELECT (SELECT COUNT(*) FROM machines WHERE approval_status='pending'), (SELECT COUNT(*) FROM enrollment_admissions)")) {
            capacity.Transaction = transaction;
            using var counts = capacity.ExecuteReader(); counts.Read();
            if (counts.GetInt64(0) >= policy.MaxPending || counts.GetInt64(1) >= policy.NewPerMinute)
                throw new EnrollmentCapacityException();
        }
        using (var insert = Command("INSERT INTO machines(machine_id,public_key,local_ip,mac_address,moonlight_port,sunshine_port) VALUES($id,$key,$ip,$mac,$moon,$sun); INSERT INTO enrollment_admissions(admitted_at) VALUES($at)",
            ("$id",request.MachineId),("$key",request.PublicKey),("$ip",request.LocalIp),("$mac",request.MacAddress),("$moon",request.MoonlightPort),("$sun",request.SunshinePort),("$at",clock.GetUtcNow().ToUnixTimeMilliseconds()))) {
            insert.Transaction = transaction; insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(request.MachineId, "pending");
    } }
    private static void ValidateNetworkMetadata(string? ip, string? mac) {
        if (string.IsNullOrWhiteSpace(ip) || ip.Length > 45 || !System.Net.IPAddress.TryParse(ip, out var address)
            || address.ToString().Contains('%') || ip != address.ToString())
            throw new ArgumentException("Invalid local IP address");
        // Agent reports six bytes without separators; common colon/dash inventory formats are accepted too.
        if (mac is null || mac.Length is not (12 or 17)
            || !System.Text.RegularExpressions.Regex.IsMatch(mac, @"\A(?:[0-9a-fA-F]{12}|(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}|(?:[0-9a-fA-F]{2}-){5}[0-9a-fA-F]{2})\z"))
            throw new ArgumentException("Invalid MAC address");
    }
    private static void Ports(int moon,int sun) {if(moon is <1 or >65535 || sun is <1 or >65535)throw new ArgumentException("Invalid port");}
    public MachineDto[] List() {lock(gate) {
        // Read heartbeat, configuration acknowledgment and allocation from the same SQLite snapshot.
        using var cmd=Command("SELECT m.machine_id,m.approval_status,m.display_name,m.hourly_price,m.local_ip,m.mac_address,m.moonlight_port,m.sunshine_port,m.revision,m.approved_by,m.approved_at,h.last_seen,h.boot_id,h.readiness,EXISTS(SELECT 1 FROM leases l WHERE l.machine_id=m.machine_id AND l.allocation_held=1),a.applied_revision FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id LEFT JOIN machine_config_acks a ON a.machine_id=m.machine_id ORDER BY m.machine_id");
        using var r=cmd.ExecuteReader(); var rows=new List<MachineDto>(); var now=clock.GetUtcNow();
        while(r.Read()) {
            DateTimeOffset? seen=r.IsDBNull(11)?null:DateTimeOffset.Parse(r.GetString(11));
            var readiness=seen is null?"unknown":now-seen.Value>TimeSpan.FromSeconds(20)?"unreachable":r.GetString(13);
            rows.Add(new MachineDto(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetInt64(8),seen,r.IsDBNull(12)?null:r.GetString(12),readiness,null,r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:DateTimeOffset.Parse(r.GetString(10))) {AppliedConfigRevision=r.IsDBNull(15)?null:r.GetInt64(15),CanRent=r.GetString(1)=="approved" && readiness=="ready" && r.GetInt64(14)==0 && !r.IsDBNull(15) && r.GetInt64(15)==r.GetInt64(8)});
        }
        r.Close();
        for(var i=0;i<rows.Count;i++){var row=rows[i];rows[i]=row with {PublicKeyFingerprint=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(PublicKey(row.MachineId)!)))};}
        return rows.ToArray();
    }}
    public MachineDto Update(string id, MachineUpdate update,long expectedRevision) {lock(gate) {
        Ports(update.MoonlightPort,update.SunshinePort); if(string.IsNullOrWhiteSpace(update.DisplayName)||update.HourlyPriceMinorUnits<=0)throw new ArgumentException("Name and positive price required");
        using var cmd=Command("UPDATE machines SET display_name=$name,hourly_price=$price,moonlight_port=$moon,sunshine_port=$sun,revision=revision+1 WHERE machine_id=$id AND revision=$revision",("$name",update.DisplayName.Trim()),("$price",update.HourlyPriceMinorUnits),("$moon",update.MoonlightPort),("$sun",update.SunshinePort),("$id",id),("$revision",expectedRevision));
        if(cmd.ExecuteNonQuery()!=1)throw new InvalidOperationException("Revision conflict or missing machine"); return List().Single(x=>x.MachineId==id);
    }}
    public MachineDto Approve(string id,string actor) {lock(gate) {
        if(string.IsNullOrWhiteSpace(actor))throw new ArgumentException("Authenticated actor required");
        var machine=List().SingleOrDefault(x=>x.MachineId==id)??throw new KeyNotFoundException();
        if(string.IsNullOrWhiteSpace(machine.DisplayName)||machine.HourlyPriceMinorUnits<=0)throw new InvalidOperationException("Name and positive price required before approval");
        using var tx=connection.BeginTransaction(); var at=DateTimeOffset.UtcNow.ToString("O");
        using var cmd=Command("UPDATE machines SET approval_status='approved',approved_by=$actor,approved_at=$at,revision=revision+1 WHERE machine_id=$id; INSERT INTO machine_approval_audit(machine_id,actor_id,approved_at) VALUES($id,$actor,$at)",("$actor",actor),("$at",at),("$id",id)); cmd.Transaction=tx;cmd.ExecuteNonQuery();tx.Commit();return List().Single(x=>x.MachineId==id);
    }}
}
