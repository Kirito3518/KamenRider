using Microsoft.Data.Sqlite;
using KamenRaider.Contracts;
using System.Security.Cryptography;
using System.Text.Json;
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
        foreach (var name in new[] { "001-machines.sql", "002-leases-commands.sql", "003-pairing.sql", "004-boot-challenges.sql", "005-config-acks.sql", "006-enrollment-tickets.sql", "007-admin-vnc-sessions.sql", "008-vnc-approved-target.sql", "009-agent-version.sql", "010-completed-leases.sql", "011-readiness-diagnostics.sql" }) {
            if(name=="003-pairing.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=3)continue;}
            if(name=="004-boot-challenges.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=4)continue;}
            if(name=="010-completed-leases.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=10)continue;}
            if(name=="011-readiness-diagnostics.sql"){using var version=connection.CreateCommand();version.Transaction=transaction;version.CommandText="PRAGMA user_version";if((long)version.ExecuteScalar()!>=11)continue;}
            if(name=="008-vnc-approved-target.sql") {
                using var columns=connection.CreateCommand();columns.Transaction=transaction;columns.CommandText="PRAGMA table_info(machines)";using var rows=columns.ExecuteReader();
                var hasVncLocalIp=false;while(rows.Read())if(rows.GetString(1)=="vnc_local_ip")hasVncLocalIp=true;
                if(hasVncLocalIp)continue;
            }
            if(name=="009-agent-version.sql") {
                using var columns=connection.CreateCommand();columns.Transaction=transaction;columns.CommandText="PRAGMA table_info(machine_heartbeats)";using var rows=columns.ExecuteReader();
                var hasAgentVersion=false;while(rows.Read())if(rows.GetString(1)=="agent_version")hasAgentVersion=true;
                if(hasAgentVersion)continue;
            }
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
    public EnrollmentTicketResponse CreateEnrollmentTicket() { lock(gate) {
        var now=clock.GetUtcNow();var expires=now.AddHours(24);var nowMilliseconds=now.ToUnixTimeMilliseconds();
        var token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        var tokenHash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));
        using var transaction=connection.BeginTransaction();
        using(var cleanup=Command("DELETE FROM agent_enrollment_tickets WHERE expires_at_unix_ms <= $now",("$now",nowMilliseconds))){cleanup.Transaction=transaction;cleanup.ExecuteNonQuery();}
        using(var insert=Command("INSERT INTO agent_enrollment_tickets(token_hash,expires_at_unix_ms) VALUES($hash,$expires)",("$hash",tokenHash),("$expires",expires.ToUnixTimeMilliseconds()))){insert.Transaction=transaction;insert.ExecuteNonQuery();}
        transaction.Commit();
        return new(token,expires);
    }}
    public EnrollmentResult Enroll(EnrollmentRequest request,string? enrollmentTicket=null) { lock(gate) {
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
        var ticketHash=GetEnrollmentTicketHash(enrollmentTicket);
        if(ticketHash is null)throw new UnauthorizedAccessException("A valid enrollment ticket is required");
        var nowMilliseconds=clock.GetUtcNow().ToUnixTimeMilliseconds();
        using(var consume=Command("DELETE FROM agent_enrollment_tickets WHERE token_hash=$hash AND expires_at_unix_ms > $now",("$hash",ticketHash),("$now",nowMilliseconds))){consume.Transaction=transaction;if(consume.ExecuteNonQuery()!=1)throw new UnauthorizedAccessException("Enrollment ticket is invalid, expired or already used");}
        // The immediate transaction serializes capacity checks and admission across connections.
        var cutoff = nowMilliseconds - (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
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
    private static string? GetEnrollmentTicketHash(string? ticket) {
        if(ticket is null||!System.Text.RegularExpressions.Regex.IsMatch(ticket,@"\A[A-Za-z0-9_-]{43}\z",System.Text.RegularExpressions.RegexOptions.CultureInvariant))return null;
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(ticket)));
    }
    public AdminVncTicketResponse CreateAdminVncTicket(string machineId,string adminUserId,string allowedOrigin) {
        Identifier(machineId);Identifier(adminUserId);var origin=AdminVncOrigin(allowedOrigin);lock(gate) {
            var now=clock.GetUtcNow();var nowMs=now.ToUnixTimeMilliseconds();
            using var transaction=connection.BeginTransaction();
            using(var cleanup=Command("DELETE FROM admin_vnc_sessions WHERE (used_at_unix_ms IS NULL AND expires_at_unix_ms <= $now) OR (used_at_unix_ms IS NOT NULL AND ended_at_unix_ms IS NULL AND used_at_unix_ms <= $abandoned) OR ended_at_unix_ms <= $old",("$now",nowMs),("$abandoned",now.AddHours(-1).AddMinutes(-5).ToUnixTimeMilliseconds()),("$old",now.AddDays(-30).ToUnixTimeMilliseconds()))) {cleanup.Transaction=transaction;cleanup.ExecuteNonQuery();}
            string address;DateTimeOffset lastSeen;
            using(var machine=Command("SELECT m.vnc_local_ip,h.last_seen,m.approval_status FROM machines m JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",machineId))) {
                machine.Transaction=transaction;using var row=machine.ExecuteReader();
                if(!row.Read()||row.IsDBNull(0)||row.GetString(2)!="approved")throw new UnauthorizedAccessException("Machine must be approved and have an administrator-confirmed VNC target");
                address=row.GetString(0);lastSeen=DateTimeOffset.Parse(row.GetString(1));
            }
            if(lastSeen>now||now-lastSeen>TimeSpan.FromSeconds(20)||!IsPrivateNetworkAddress(address))throw new UnauthorizedAccessException("Machine is not currently reachable on a private network");
            var token=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
            var hash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)));var sessionId=Guid.NewGuid().ToString("N");var expires=now.AddMinutes(1);
            using(var insert=Command("INSERT INTO admin_vnc_sessions(session_id,ticket_hash,machine_id,admin_user_id,allowed_origin,issued_at_unix_ms,expires_at_unix_ms) VALUES($session,$hash,$machine,$admin,$origin,$issued,$expires)",("$session",sessionId),("$hash",hash),("$machine",machineId),("$admin",adminUserId),("$origin",origin),("$issued",nowMs),("$expires",expires.ToUnixTimeMilliseconds()))) {insert.Transaction=transaction;insert.ExecuteNonQuery();}
            transaction.Commit();return new(token,expires);
        }
    }
    public AdminVncTarget ConsumeAdminVncTicket(string ticket,string origin) {
        if(ticket is null||!System.Text.RegularExpressions.Regex.IsMatch(ticket,@"\A[A-Za-z0-9_-]{43}\z",System.Text.RegularExpressions.RegexOptions.CultureInvariant))throw new UnauthorizedAccessException("VNC session ticket is invalid");
        var hash=Convert.ToHexString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(ticket)));var requestOrigin=AdminVncOrigin(origin);lock(gate) {
            var now=clock.GetUtcNow();var nowMs=now.ToUnixTimeMilliseconds();using var transaction=connection.BeginTransaction();
            string sessionId,machineId,adminUserId,allowedOrigin;
            using(var ticketRow=Command("SELECT session_id,machine_id,admin_user_id,allowed_origin FROM admin_vnc_sessions WHERE ticket_hash=$hash AND expires_at_unix_ms > $now AND used_at_unix_ms IS NULL",("$hash",hash),("$now",nowMs))) {
                ticketRow.Transaction=transaction;using var row=ticketRow.ExecuteReader();if(!row.Read())throw new UnauthorizedAccessException("VNC session ticket is expired or already used");
                sessionId=row.GetString(0);machineId=row.GetString(1);adminUserId=row.GetString(2);allowedOrigin=row.GetString(3);
            }
            if(!string.Equals(allowedOrigin,requestOrigin,StringComparison.Ordinal))throw new UnauthorizedAccessException("VNC session origin is invalid");
            string address;DateTimeOffset lastSeen;
            using(var machine=Command("SELECT m.vnc_local_ip,h.last_seen,m.approval_status FROM machines m JOIN machine_heartbeats h ON h.machine_id=m.machine_id WHERE m.machine_id=$id",("$id",machineId))) {
                machine.Transaction=transaction;using var row=machine.ExecuteReader();if(!row.Read()||row.IsDBNull(0)||row.GetString(2)!="approved")throw new UnauthorizedAccessException("Machine must be approved and have an administrator-confirmed VNC target");address=row.GetString(0);lastSeen=DateTimeOffset.Parse(row.GetString(1));
            }
            if(lastSeen>now||now-lastSeen>TimeSpan.FromSeconds(20)||!IsPrivateNetworkAddress(address))throw new UnauthorizedAccessException("Machine is not currently reachable on a private network");
            using(var consume=Command("UPDATE admin_vnc_sessions SET used_at_unix_ms=$now WHERE ticket_hash=$hash AND used_at_unix_ms IS NULL AND expires_at_unix_ms > $now",("$now",nowMs),("$hash",hash))) {consume.Transaction=transaction;if(consume.ExecuteNonQuery()!=1)throw new UnauthorizedAccessException("VNC session ticket is expired or already used");}
            transaction.Commit();return new(sessionId,machineId,adminUserId,System.Net.IPAddress.Parse(address),5900,allowedOrigin);
        }
    }
    public void FinishAdminVncSession(string sessionId) {if(string.IsNullOrWhiteSpace(sessionId)||sessionId.Length>64)return;lock(gate)using(var command=Command("UPDATE admin_vnc_sessions SET ended_at_unix_ms=$now WHERE session_id=$session AND used_at_unix_ms IS NOT NULL AND ended_at_unix_ms IS NULL",("$now",clock.GetUtcNow().ToUnixTimeMilliseconds()),("$session",sessionId)))command.ExecuteNonQuery();}
    private static string AdminVncOrigin(string value) {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var origin)||origin.UserInfo!=""||origin.Query!=""||origin.Fragment!=""||origin.AbsolutePath!="/")throw new ArgumentException("Invalid admin VNC origin");
        if(origin.Scheme=="http") {
            var host=origin.Host.Trim('[',']');
            if(!host.Equals("localhost",StringComparison.OrdinalIgnoreCase)&&(!System.Net.IPAddress.TryParse(host,out var address)||!System.Net.IPAddress.IsLoopback(address)))throw new ArgumentException("HTTP admin VNC origins must use loopback");
        } else if(origin.Scheme!="https")throw new ArgumentException("Invalid admin VNC origin");
        return origin.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }
    private static string? VncTargetAddress(string? value) {
        if(string.IsNullOrEmpty(value))return null;
        if(value.Length>45||value!=value.Trim()||!System.Net.IPAddress.TryParse(value,out var address)||address.ToString().Contains('%')||!IsPrivateNetworkAddress(address.ToString()))throw new ArgumentException("VNC target must be a private LAN IP address");
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        return address.ToString();
    }
    private static bool IsPrivateNetworkAddress(string value) {
        if(!System.Net.IPAddress.TryParse(value,out var address))return false;
        if(address.IsIPv4MappedToIPv6)address=address.MapToIPv4();
        var bytes=address.GetAddressBytes();
        if(address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetwork)
            return bytes[0]==10||bytes[0]==172&&bytes[1] is >=16 and <=31||bytes[0]==192&&bytes[1]==168;
        return address.AddressFamily==System.Net.Sockets.AddressFamily.InterNetworkV6&&address.IsIPv6UniqueLocal;
    }
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
        var now=clock.GetUtcNow();
        using var cmd=Command("SELECT m.machine_id,m.approval_status,m.display_name,m.hourly_price,m.local_ip,m.mac_address,m.moonlight_port,m.sunshine_port,m.revision,m.approved_by,m.approved_at,h.last_seen,h.boot_id,h.readiness,EXISTS(SELECT 1 FROM leases l WHERE l.machine_id=m.machine_id AND l.allocation_held=1),a.applied_revision,m.vnc_local_ip,h.agent_version,(SELECT COUNT(*) FROM admin_vnc_sessions v WHERE v.machine_id=m.machine_id AND v.used_at_unix_ms IS NOT NULL AND v.ended_at_unix_ms IS NULL AND v.used_at_unix_ms > $activeVncSince),d.diagnostics_json FROM machines m LEFT JOIN machine_heartbeats h ON h.machine_id=m.machine_id LEFT JOIN machine_config_acks a ON a.machine_id=m.machine_id LEFT JOIN machine_readiness_diagnostics d ON d.machine_id=m.machine_id ORDER BY m.machine_id",("$activeVncSince",now.AddHours(-1).ToUnixTimeMilliseconds()));
        using var r=cmd.ExecuteReader(); var rows=new List<MachineDto>();
        while(r.Read()) {
            DateTimeOffset? seen=r.IsDBNull(11)?null:DateTimeOffset.Parse(r.GetString(11));
            var readiness=seen is null?"unknown":now-seen.Value>TimeSpan.FromSeconds(20)?"unreachable":r.GetString(13);
            rows.Add(new MachineDto(r.GetString(0),r.GetString(1),r.GetString(2),r.GetInt64(3),r.GetString(4),r.GetString(5),r.GetInt32(6),r.GetInt32(7),r.GetInt64(8),seen,r.IsDBNull(12)?null:r.GetString(12),readiness,r.IsDBNull(17)?null:r.GetString(17),r.IsDBNull(9)?null:r.GetString(9),r.IsDBNull(10)?null:DateTimeOffset.Parse(r.GetString(10))) {AppliedConfigRevision=r.IsDBNull(15)?null:r.GetInt64(15),VncLocalIp=r.IsDBNull(16)?null:r.GetString(16),ActiveAdminVncSessions=r.GetInt32(18),CanRent=r.GetString(1)=="approved" && readiness=="ready" && r.GetInt64(14)==0 && !r.IsDBNull(15) && r.GetInt64(15)==r.GetInt64(8),ReadinessDiagnostics=r.IsDBNull(19)?null:JsonSerializer.Deserialize<ReadinessDiagnostic[]>(r.GetString(19))});
        }
        r.Close();
        for(var i=0;i<rows.Count;i++){var row=rows[i];rows[i]=row with {PublicKeyFingerprint=Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(PublicKey(row.MachineId)!)))};}
        return rows.ToArray();
    }}
    public MachineDto Update(string id, MachineUpdate update,long expectedRevision) {lock(gate) {
        Ports(update.MoonlightPort,update.SunshinePort); if(string.IsNullOrWhiteSpace(update.DisplayName)||update.HourlyPriceMinorUnits<=0)throw new ArgumentException("Name and positive price required");
        var vncLocalIp=VncTargetAddress(update.VncLocalIp);
        using var cmd=Command("UPDATE machines SET display_name=$name,hourly_price=$price,moonlight_port=$moon,sunshine_port=$sun,vnc_local_ip=$vnc,revision=revision+1 WHERE machine_id=$id AND revision=$revision",("$name",update.DisplayName.Trim()),("$price",update.HourlyPriceMinorUnits),("$moon",update.MoonlightPort),("$sun",update.SunshinePort),("$vnc",vncLocalIp),("$id",id),("$revision",expectedRevision));
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
