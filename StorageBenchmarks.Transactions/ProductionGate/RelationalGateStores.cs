using Dapper;
using Idb.Libraries.Abstractions.Enums;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;
using System.Data;

internal sealed class SqliteGateStore : IProductionGateStore
{
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly StorageDurabilityMode _durabilityMode;
    private readonly SqliteClientKind _clientKind;
    private readonly bool _createSecondaryIndexes;
    private readonly ConcurrentBag<SqliteConnection> _pool = [];
    private int _disposed;

    public SqliteGateStore(
        string databasePath,
        StorageDurabilityMode durabilityMode,
        SqliteClientKind clientKind,
        bool createSecondaryIndexes = true)
    {
        _databasePath = databasePath;
        _durabilityMode = durabilityMode;
        _clientKind = clientKind;
        _createSecondaryIndexes = createSecondaryIndexes;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString();
    }

    public string EngineName => (_clientKind == SqliteClientKind.Dapper
        ? "SQLite Dapper"
        : "SQLite Microsoft.Data.Sqlite")
        + (_createSecondaryIndexes ? string.Empty : " (no secondary indexes)");
    public string CacheModeName => "PageCache";
    public string DurabilityModeName => _durabilityMode == StorageDurabilityMode.Durable ? "Full" : "Normal";

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_databasePath))!);
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE IF NOT EXISTS Records(
                    Id TEXT NOT NULL PRIMARY KEY,
                    Name TEXT NOT NULL,
                    Age INTEGER NOT NULL,
                    Role TEXT NOT NULL,
                    Content TEXT NOT NULL,
                    Revision INTEGER NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (_createSecondaryIndexes)
            {
                command.CommandText = """
                    CREATE INDEX IF NOT EXISTS IX_Records_Role_Age ON Records(Role, Age);
                    CREATE INDEX IF NOT EXISTS IX_Records_Name ON Records(Name);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Return(connection);
        }
    }

    public async ValueTask SeedAsync(IReadOnlyList<GateRecord> records, CancellationToken cancellationToken = default)
    {
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clientKind == SqliteClientKind.Dapper)
            {
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var changed = await connection.ExecuteAsync(new CommandDefinition(
                        InsertSql,
                        record,
                        cancellationToken: cancellationToken)).ConfigureAwait(false);
                    if (changed != 1)
                    {
                        throw new InvalidOperationException(
                            $"SQLite Dapper inserted {changed} rows for '{record.Id}'; expected one.");
                    }
                }

                return;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = InsertSql;
            var id = command.Parameters.Add("@Id", SqliteType.Text);
            var name = command.Parameters.Add("@Name", SqliteType.Text);
            var age = command.Parameters.Add("@Age", SqliteType.Integer);
            var role = command.Parameters.Add("@Role", SqliteType.Text);
            var content = command.Parameters.Add("@Content", SqliteType.Text);
            var revision = command.Parameters.Add("@Revision", SqliteType.Integer);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                id.Value = record.Id;
                name.Value = record.Name;
                age.Value = record.Age;
                role.Value = record.Role;
                content.Value = record.Content;
                revision.Value = record.Revision;
                var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new InvalidOperationException(
                        $"SQLite Microsoft.Data.Sqlite inserted {changed} rows for '{record.Id}'; expected one.");
                }
            }
        }
        finally
        {
            Return(connection);
        }
    }

    public async ValueTask<GateQueryResult> QueryAsync(
        GateQueryKind kind,
        string exactId,
        CancellationToken cancellationToken = default)
    {
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sql = kind switch
            {
                GateQueryKind.Exact => SelectSql + " WHERE Id = @value",
                GateQueryKind.Range => SelectSql + " WHERE Age >= 75",
                GateQueryKind.Prefix => SelectSql + " WHERE Name LIKE 'customer-00000%'",
                GateQueryKind.Suffix => SelectSql + " WHERE Name LIKE '%special-tail'",
                GateQueryKind.Contains => SelectSql + " WHERE instr(Content, 'rare-needle') > 0",
                GateQueryKind.BroadConjunction => SelectSql + " WHERE Role = 'student' AND Age >= 40",
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            };

            if (_clientKind == SqliteClientKind.Dapper)
            {
                var parameters = kind == GateQueryKind.Exact ? new { value = exactId } : null;
                var materialized = (await connection.QueryAsync<GateRecord>(new CommandDefinition(
                    sql,
                    parameters,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
                return MaterializedResult(materialized);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            if (kind == GateQueryKind.Exact)
            {
                command.Parameters.AddWithValue("@value", exactId);
            }

            return await MaterializeAsync(command, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Return(connection);
        }
    }

    public async ValueTask InsertAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        _ = await ExecuteWriteAsync(
            InsertSql,
            record,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> UpdateAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        var changed = await ExecuteWriteAsync(
            "UPDATE Records SET Age = @Age, Content = @Content, Revision = @Revision WHERE Id = @Id",
            record,
            cancellationToken).ConfigureAwait(false);
        return changed == 1;
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clientKind == SqliteClientKind.Dapper)
            {
                return await connection.ExecuteAsync(new CommandDefinition(
                    "DELETE FROM Records WHERE Id = @id",
                    new { id },
                    cancellationToken: cancellationToken)).ConfigureAwait(false) == 1;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Records WHERE Id = @id";
            command.Parameters.AddWithValue("@id", id);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }
        finally
        {
            Return(connection);
        }
    }

    public async ValueTask<Dictionary<string, GateRecord>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clientKind == SqliteClientKind.Dapper)
            {
                var records = (await connection.QueryAsync<GateRecord>(new CommandDefinition(
                    SelectSql,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
                return records.ToDictionary(static record => record.Id, StringComparer.Ordinal);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = SelectSql;
            var result = new Dictionary<string, GateRecord>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var record = ReadRecord(reader);
                result.Add(record.Id, record);
            }

            return result;
        }
        finally
        {
            Return(connection);
        }
    }

    public async ValueTask ReopenAsync(CancellationToken cancellationToken = default)
    {
        DisposePool();
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        Return(connection);
    }

    public async ValueTask CheckpointWalAsync(
        bool truncate,
        CancellationToken cancellationToken = default)
    {
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = truncate
                ? "PRAGMA wal_checkpoint(TRUNCATE);"
                : "PRAGMA wal_checkpoint(FULL);";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Return(connection);
        }
    }

    public async ValueTask VacuumAsync(CancellationToken cancellationToken = default)
    {
        DisposePool();
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Return(connection);
        }
    }

    public async ValueTask<bool> VerifyAtomicRollbackAsync(
        GateRecord first,
        GateRecord second,
        CancellationToken cancellationToken = default)
    {
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var transaction = (SqliteTransaction)await connection
                             .BeginTransactionAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = InsertSql;
                AddRecordParameters(command);
                BindRecord(command, first);
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                BindRecord(command, second);
                _ = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT COUNT(*) FROM Records WHERE Id IN (@first, @second)";
            verify.Parameters.AddWithValue("@first", first.Id);
            verify.Parameters.AddWithValue("@second", second.Id);
            var count = Convert.ToInt32(
                await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            return count == 0;
        }
        finally
        {
            Return(connection);
        }
    }

    public GateStorageStats GetStorageStats()
    {
        long bytes = 0;
        var files = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var path = _databasePath + suffix;
            if (!File.Exists(path))
            {
                continue;
            }

            bytes += new FileInfo(path).Length;
            files++;
        }

        return new GateStorageStats(bytes, files);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            DisposePool();
            SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        }

        return ValueTask.CompletedTask;
    }

    private const string InsertSql = """
        INSERT INTO Records(Id, Name, Age, Role, Content, Revision)
        VALUES(@Id, @Name, @Age, @Role, @Content, @Revision)
        """;
    private const string SelectSql = "SELECT Id, Name, Age, Role, Content, Revision FROM Records";

    private async ValueTask<int> ExecuteWriteAsync(
        string sql,
        GateRecord record,
        CancellationToken cancellationToken)
    {
        var connection = await RentAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_clientKind == SqliteClientKind.Dapper)
            {
                return await connection.ExecuteAsync(new CommandDefinition(
                    sql,
                    record,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@Id", record.Id);
            command.Parameters.AddWithValue("@Name", record.Name);
            command.Parameters.AddWithValue("@Age", record.Age);
            command.Parameters.AddWithValue("@Role", record.Role);
            command.Parameters.AddWithValue("@Content", record.Content);
            command.Parameters.AddWithValue("@Revision", record.Revision);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Return(connection);
        }
    }

    private static async ValueTask<GateQueryResult> MaterializeAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var materialized = new List<GateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            materialized.Add(ReadRecord(reader));
        }

        long checksum = 0;
        foreach (var record in materialized)
        {
            checksum += record.MaterializedChecksum;
        }

        GC.KeepAlive(materialized);
        return new GateQueryResult(materialized.Count, checksum);
    }

    private static GateQueryResult MaterializedResult(List<GateRecord> materialized)
    {
        long checksum = 0;
        foreach (var record in materialized)
        {
            checksum += record.MaterializedChecksum;
        }

        GC.KeepAlive(materialized);
        return new GateQueryResult(materialized.Count, checksum);
    }

    private static GateRecord ReadRecord(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5));

    private static void AddRecordParameters(SqliteCommand command)
    {
        _ = command.Parameters.Add("@Id", SqliteType.Text);
        _ = command.Parameters.Add("@Name", SqliteType.Text);
        _ = command.Parameters.Add("@Age", SqliteType.Integer);
        _ = command.Parameters.Add("@Role", SqliteType.Text);
        _ = command.Parameters.Add("@Content", SqliteType.Text);
        _ = command.Parameters.Add("@Revision", SqliteType.Integer);
    }

    private static void BindRecord(SqliteCommand command, GateRecord record)
    {
        command.Parameters["@Id"].Value = record.Id;
        command.Parameters["@Name"].Value = record.Name;
        command.Parameters["@Age"].Value = record.Age;
        command.Parameters["@Role"].Value = record.Role;
        command.Parameters["@Content"].Value = record.Content;
        command.Parameters["@Revision"].Value = record.Revision;
    }

    private async ValueTask<SqliteConnection> RentAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_pool.TryTake(out var pooled))
        {
            return pooled;
        }

        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = _durabilityMode == StorageDurabilityMode.Durable
            ? "PRAGMA busy_timeout=30000; PRAGMA synchronous=FULL;"
            : "PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private void Return(SqliteConnection connection)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _pool.Add(connection);
        }
        else
        {
            connection.Dispose();
        }
    }

    private void DisposePool()
    {
        while (_pool.TryTake(out var connection))
        {
            connection.Dispose();
        }
    }
}

internal sealed class SqlServerGateStore : IProductionGateStore
{
    private readonly string _connectionString;
    private readonly string _tableName = "InterfaceDbProductionGate_" + Guid.NewGuid().ToString("N");
    private readonly bool _dropOnDispose;
    private int _initialized;

    public SqlServerGateStore(string connectionString, bool dropOnDispose)
    {
        _connectionString = connectionString;
        _dropOnDispose = dropOnDispose;
    }

    public string EngineName => "SQL Server";
    public string CacheModeName => "BufferPool";
    public string DurabilityModeName => "Committed";
    private string QualifiedTable => $"[dbo].[{_tableName}]";

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE {QualifiedTable}(
                Id NVARCHAR(64) NOT NULL PRIMARY KEY,
                Name NVARCHAR(200) NOT NULL,
                Age INT NOT NULL,
                Role NVARCHAR(40) NOT NULL,
                Content NVARCHAR(1000) NOT NULL,
                Revision BIGINT NOT NULL
            );
            CREATE INDEX [IX_{_tableName}_Role_Age] ON {QualifiedTable}(Role, Age);
            CREATE INDEX [IX_{_tableName}_Name] ON {QualifiedTable}(Name);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _initialized, 1);
    }

    public async ValueTask SeedAsync(IReadOnlyList<GateRecord> records, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO {QualifiedTable}(Id, Name, Age, Role, Content, Revision)
            VALUES(@id, @name, @age, @role, @content, @revision)
            """;
        var id = command.Parameters.Add("@id", SqlDbType.NVarChar, 64);
        var name = command.Parameters.Add("@name", SqlDbType.NVarChar, 200);
        var age = command.Parameters.Add("@age", SqlDbType.Int);
        var role = command.Parameters.Add("@role", SqlDbType.NVarChar, 40);
        var content = command.Parameters.Add("@content", SqlDbType.NVarChar, 1000);
        var revision = command.Parameters.Add("@revision", SqlDbType.BigInt);
        await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            id.Value = record.Id;
            name.Value = record.Name;
            age.Value = record.Age;
            role.Value = record.Role;
            content.Value = record.Content;
            revision.Value = record.Revision;
            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed != 1)
            {
                throw new InvalidOperationException(
                    $"SQL Server inserted {changed} rows for '{record.Id}'; expected one.");
            }
        }
    }

    public async ValueTask<GateQueryResult> QueryAsync(
        GateQueryKind kind,
        string exactId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = kind switch
        {
            GateQueryKind.Exact => SelectSql + " WHERE Id = @value",
            GateQueryKind.Range => SelectSql + " WHERE Age >= 75",
            GateQueryKind.Prefix => SelectSql + " WHERE Name LIKE 'customer-00000%'",
            GateQueryKind.Suffix => SelectSql + " WHERE Name LIKE '%special-tail'",
            GateQueryKind.Contains => SelectSql + " WHERE CHARINDEX('rare-needle', Content) > 0",
            GateQueryKind.BroadConjunction => SelectSql + " WHERE Role = 'student' AND Age >= 40",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
        if (kind == GateQueryKind.Exact)
        {
            command.Parameters.Add("@value", SqlDbType.NVarChar, 64).Value = exactId;
        }

        var materialized = new List<GateRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            materialized.Add(ReadRecord(reader));
        }

        long checksum = 0;
        foreach (var record in materialized)
        {
            checksum += record.MaterializedChecksum;
        }

        GC.KeepAlive(materialized);
        return new GateQueryResult(materialized.Count, checksum);
    }

    public async ValueTask InsertAsync(GateRecord record, CancellationToken cancellationToken = default)
    {
        _ = await ExecuteWriteAsync(
            $"INSERT INTO {QualifiedTable}(Id, Name, Age, Role, Content, Revision) VALUES(@id, @name, @age, @role, @content, @revision)",
            record,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<bool> UpdateAsync(GateRecord record, CancellationToken cancellationToken = default) =>
        await ExecuteWriteAsync(
            $"UPDATE {QualifiedTable} SET Age = @age, Content = @content, Revision = @revision WHERE Id = @id",
            record,
            cancellationToken).ConfigureAwait(false) == 1;

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {QualifiedTable} WHERE Id = @id";
        command.Parameters.Add("@id", SqlDbType.NVarChar, 64).Value = id;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async ValueTask<Dictionary<string, GateRecord>> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql;
        var result = new Dictionary<string, GateRecord>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var record = ReadRecord(reader);
            result.Add(record.Id, record);
        }

        return result;
    }

    public async ValueTask ReopenAsync(CancellationToken cancellationToken = default)
    {
        SqlConnection.ClearAllPools();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    public GateStorageStats GetStorageStats() => default;

    public async ValueTask DisposeAsync()
    {
        if (!_dropOnDispose || Interlocked.Exchange(ref _initialized, 0) == 0)
        {
            return;
        }

        try
        {
            await using var connection = await OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP TABLE IF EXISTS {QualifiedTable}";
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        finally
        {
            SqlConnection.ClearAllPools();
        }
    }

    private string SelectSql => $"SELECT Id, Name, Age, Role, Content, Revision FROM {QualifiedTable}";

    private async ValueTask<int> ExecuteWriteAsync(string sql, GateRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add("@id", SqlDbType.NVarChar, 64).Value = record.Id;
        command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = record.Name;
        command.Parameters.Add("@age", SqlDbType.Int).Value = record.Age;
        command.Parameters.Add("@role", SqlDbType.NVarChar, 40).Value = record.Role;
        command.Parameters.Add("@content", SqlDbType.NVarChar, 1000).Value = record.Content;
        command.Parameters.Add("@revision", SqlDbType.BigInt).Value = record.Revision;
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static GateRecord ReadRecord(SqlDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetInt32(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5));
}
