using Microsoft.Data.Sqlite;

namespace ReachTether.Server.Services;

public sealed class SqliteConnectionFactory(
    IConfiguration configuration,
    IWebHostEnvironment environment) : ISqliteConnectionFactory
{
    public string DatabasePath { get; } = Path.GetFullPath(Path.Combine(
        environment.ContentRootPath,
        configuration["Memory:DatabasePath"] ?? "data/reachtether-server.db"));

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            """;
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        return connection;
    }
}
