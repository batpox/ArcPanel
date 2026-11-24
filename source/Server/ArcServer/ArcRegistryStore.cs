using Microsoft.Data.Sqlite;

namespace ArcServer;

public sealed record ArcIdRecord(
    string ArcId,
    string Role,
    int InstanceNumber,
    bool IsOperational,
    string RoomId,
    string RoomName,
    string SetpointName,
    bool Enabled,
    string? DeviceIdentity,
    string? LastSeenUtc,
    string? LastKnownIp,
    string? FirmwareVersion);

public sealed record ArcIdInput(
    string ArcId,
    string RoomId,
    string RoomName,
    string SetpointName,
    bool Enabled = true);

public sealed record ArcDeviceRecord(
    string DeviceIdentity,
    string? ArcId,
    string? RoomName,
    string LastSeenUtc,
    string? LastKnownIp,
    string? FirmwareVersion);

/// <summary>Persists reusable Arc IDs and the physical ESP32 devices currently using them.</summary>
public sealed class ArcRegistryStore
{
    private readonly string _connectionString;
    public ArcRegistryStore(string databasePath)
    {
        string fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
        Initialize();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private void Initialize()
    {
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS ArcIds (
                    ArcId TEXT PRIMARY KEY,
                    Role TEXT NOT NULL,
                    InstanceNumber INTEGER NOT NULL CHECK (InstanceNumber > 0),
                    IsOperational INTEGER NOT NULL,
                    RoomId TEXT NOT NULL,
                    RoomName TEXT NOT NULL,
                    SetpointName TEXT NOT NULL,
                    Enabled INTEGER NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    ModifiedUtc TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS ArcDevices (
                    DeviceIdentity TEXT PRIMARY KEY,
                    ArcId TEXT NULL UNIQUE REFERENCES ArcIds(ArcId) ON DELETE SET NULL,
                    LastSeenUtc TEXT NOT NULL,
                    LastKnownIp TEXT NULL,
                    FirmwareVersion TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS IX_ArcDevices_LastSeenUtc ON ArcDevices(LastSeenUtc);
                """;
            command.ExecuteNonQuery();
        }

        CreateArcIdIfMissing(new ArcIdInput("GuestBedroom-1", "GuestBedroom", "Guest Bedroom", "GuestBedroomSetpoint"));
        CreateArcIdIfMissing(new ArcIdInput("GuestBedroom-2", "GuestBedroom", "Guest Bedroom (Test)", "GuestBedroomSetpoint"));
    }

    public IReadOnlyList<ArcIdRecord> GetArcIds()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.ArcId, a.Role, a.InstanceNumber, a.IsOperational, a.RoomId, a.RoomName,
                   a.SetpointName, a.Enabled, d.DeviceIdentity, d.LastSeenUtc, d.LastKnownIp, d.FirmwareVersion
            FROM ArcIds a LEFT JOIN ArcDevices d ON d.ArcId = a.ArcId
            ORDER BY a.Role, a.InstanceNumber;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<ArcIdRecord>();
        while (reader.Read()) rows.Add(ReadArcId(reader));
        return rows;
    }

    public IReadOnlyList<ArcIdRecord> GetAvailableArcIds()
        => GetArcIds().Where(x => x.Enabled && x.DeviceIdentity is null).ToArray();

    public IReadOnlyList<ArcIdRecord> GetPanels()
        => GetArcIds().Where(x => x.DeviceIdentity is not null).ToArray();

    public IReadOnlyList<ArcDeviceRecord> GetDevices()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.DeviceIdentity, d.ArcId, a.RoomName, d.LastSeenUtc, d.LastKnownIp, d.FirmwareVersion
            FROM ArcDevices d LEFT JOIN ArcIds a ON a.ArcId = d.ArcId
            ORDER BY d.LastSeenUtc DESC;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<ArcDeviceRecord>();
        while (reader.Read()) rows.Add(new ArcDeviceRecord(reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5)));
        return rows;
    }

    public ArcIdRecord? GetAssignment(string deviceIdentity)
        => GetArcIds().FirstOrDefault(x => string.Equals(x.DeviceIdentity, deviceIdentity, StringComparison.Ordinal));

    public void RegisterDevice(string deviceIdentity, string? remoteAddress, string? firmwareVersion)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ArcDevices(DeviceIdentity, LastSeenUtc, LastKnownIp, FirmwareVersion)
            VALUES($identity, $now, $ip, $firmware)
            ON CONFLICT(DeviceIdentity) DO UPDATE SET
                LastSeenUtc = excluded.LastSeenUtc,
                LastKnownIp = COALESCE(excluded.LastKnownIp, ArcDevices.LastKnownIp),
                FirmwareVersion = COALESCE(excluded.FirmwareVersion, ArcDevices.FirmwareVersion);
            """;
        command.Parameters.AddWithValue("$identity", deviceIdentity);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$ip", (object?)remoteAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$firmware", (object?)firmwareVersion ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    /// <summary>Assigns an ID only if it is free; administrative reassignment is a separate operation.</summary>
    public bool TryClaim(string deviceIdentity, string arcId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ArcDevices SET ArcId = $arcId
            WHERE DeviceIdentity = $identity AND ArcId IS NULL
              AND EXISTS (SELECT 1 FROM ArcIds WHERE ArcId = $arcId AND Enabled = 1)
              AND NOT EXISTS (SELECT 1 FROM ArcDevices WHERE ArcId = $arcId);
            """;
        command.Parameters.AddWithValue("$arcId", arcId);
        command.Parameters.AddWithValue("$identity", deviceIdentity);
        try
        {
            bool claimed = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return claimed;
        }
        catch (SqliteException)
        {
            transaction.Rollback();
            return false;
        }
    }

    /// <summary>Moves a configured Arc ID to another known device, clearing the old device's assignment.</summary>
    public bool Reassign(string arcId, string deviceIdentity)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM ArcIds WHERE ArcId=$id AND Enabled=1), EXISTS(SELECT 1 FROM ArcDevices WHERE DeviceIdentity=$device);";
            exists.Parameters.AddWithValue("$id", arcId);
            exists.Parameters.AddWithValue("$device", deviceIdentity);
            using var reader = exists.ExecuteReader();
            reader.Read();
            if (reader.GetInt32(0) != 1 || reader.GetInt32(1) != 1) { transaction.Rollback(); return false; }
        }
        using (var detach = connection.CreateCommand())
        {
            detach.Transaction = transaction;
            detach.CommandText = "UPDATE ArcDevices SET ArcId = NULL WHERE ArcId = $arcId OR DeviceIdentity = $identity;";
            detach.Parameters.AddWithValue("$arcId", arcId);
            detach.Parameters.AddWithValue("$identity", deviceIdentity);
            detach.ExecuteNonQuery();
        }
        using var assign = connection.CreateCommand();
        assign.Transaction = transaction;
        assign.CommandText = "UPDATE ArcDevices SET ArcId = $arcId WHERE DeviceIdentity = $identity;";
        assign.Parameters.AddWithValue("$arcId", arcId);
        assign.Parameters.AddWithValue("$identity", deviceIdentity);
        bool assigned = assign.ExecuteNonQuery() == 1;
        transaction.Commit();
        return assigned;
    }

    public bool CreateArcId(ArcIdInput input)
    {
        if (!TryParseArcId(input.ArcId, out string role, out int number) ||
            string.IsNullOrWhiteSpace(input.RoomId) || string.IsNullOrWhiteSpace(input.RoomName)) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ArcIds(ArcId, Role, InstanceNumber, IsOperational, RoomId, RoomName,
                               SetpointName, Enabled, CreatedUtc, ModifiedUtc)
            VALUES($id, $role, $number, $operational, $room, $name, $setpoint, $enabled, $now, $now);
            """;
        AddArcIdParameters(command, input, role, number);
        try { return command.ExecuteNonQuery() == 1; }
        catch (SqliteException) { return false; }
    }

    public bool UpdateArcId(string arcId, ArcIdInput input)
    {
        if (!string.Equals(arcId, input.ArcId, StringComparison.Ordinal) ||
            !TryParseArcId(input.ArcId, out string role, out int number) ||
            string.IsNullOrWhiteSpace(input.RoomId) || string.IsNullOrWhiteSpace(input.RoomName)) return false;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ArcIds SET Role=$role, InstanceNumber=$number, IsOperational=$operational,
                RoomId=$room, RoomName=$name, SetpointName=$setpoint, Enabled=$enabled, ModifiedUtc=$now
            WHERE ArcId=$id;
            """;
        AddArcIdParameters(command, input, role, number);
        return command.ExecuteNonQuery() == 1;
    }

    public bool DeleteArcId(string arcId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM ArcIds WHERE ArcId=$id AND NOT EXISTS(SELECT 1 FROM ArcDevices WHERE ArcId=$id);";
        command.Parameters.AddWithValue("$id", arcId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool Unassign(string deviceIdentity)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ArcDevices SET ArcId=NULL WHERE DeviceIdentity=$identity AND ArcId IS NOT NULL;";
        command.Parameters.AddWithValue("$identity", deviceIdentity);
        return command.ExecuteNonQuery() == 1;
    }

    private void CreateArcIdIfMissing(ArcIdInput input)
    {
        if (!GetArcIds().Any(x => x.ArcId == input.ArcId)) _ = CreateArcId(input);
    }

    private static void AddArcIdParameters(SqliteCommand command, ArcIdInput input, string role, int number)
    {
        command.Parameters.AddWithValue("$id", input.ArcId);
        command.Parameters.AddWithValue("$role", role);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$operational", number == 1 ? 1 : 0);
        command.Parameters.AddWithValue("$room", input.RoomId);
        command.Parameters.AddWithValue("$name", input.RoomName);
        command.Parameters.AddWithValue("$setpoint", input.SetpointName ?? string.Empty);
        command.Parameters.AddWithValue("$enabled", input.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
    }

    private static ArcIdRecord ReadArcId(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3) == 1,
        reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7) == 1,
        reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11));

    public static bool TryParseArcId(string? arcId, out string role, out int number)
    {
        role = string.Empty;
        number = 0;
        if (string.IsNullOrWhiteSpace(arcId)) return false;
        int separator = arcId.LastIndexOf('-');
        if (separator <= 0 || separator == arcId.Length - 1 ||
            !int.TryParse(arcId[(separator + 1)..], out number) || number < 1) return false;
        role = arcId[..separator];
        return role.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '_');
    }
}
