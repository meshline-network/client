namespace Meshline.Cli.Tests;

internal sealed class TemporaryProfile : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "meshline-cli-test-" + Guid.NewGuid().ToString("N"));
    public ProfileContext Context { get; }
    public TemporaryProfile(string mode = "file")
    {
        PrivateFiles.EnsureDirectory(DirectoryPath);
        Context = new(Path.Combine(DirectoryPath, "config.json"), "test", new()
        {
            Id = Guid.NewGuid().ToString("N"), Network = "neo:123:0x" + new string('a', 40), RpcUrl = "http://localhost:10332",
            DataDirectory = Path.Combine(DirectoryPath, "profile"), Protection = new(mode, Path.Combine(DirectoryPath, "credential.key"))
        });
        File.Copy(Path.Combine(AppContext.BaseDirectory, "config.json"), Context.ConfigPath);
        PrivateFiles.EnsureDirectory(Context.DataDirectory);
    }
    public void ReleaseDatabaseConnections()
    {
        // EF disposes connections into SQLite pools; release only this fixture's pools before deleting its files.
        foreach (var mode in new[] { Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite, Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate })
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Context.DatabasePath, Mode = mode }.ToString());
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(connection);
        }
    }
    public void Dispose()
    {
        ReleaseDatabaseConnections();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, true);
    }
}
