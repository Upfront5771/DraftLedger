using Microsoft.Data.Sqlite;

namespace DraftLedger.Core;

// Entirely derived data. Manuscripts and project.json remain authoritative.
public sealed class LibraryIndex : IDisposable
{
    private readonly SqliteConnection connection;
    public LibraryIndex(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connection = new(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS stories(id TEXT PRIMARY KEY, title TEXT NOT NULL, folder TEXT NOT NULL, words INTEGER NOT NULL, modified TEXT NOT NULL, archived INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
    }
    public void Rebuild(IEnumerable<Story> stories)
    {
        using var transaction = connection.BeginTransaction();
        using (var clear = connection.CreateCommand()) { clear.Transaction = transaction; clear.CommandText = "DELETE FROM stories"; clear.ExecuteNonQuery(); }
        foreach (var story in stories)
        {
            using var cmd = connection.CreateCommand(); cmd.Transaction = transaction;
            cmd.CommandText = "INSERT INTO stories VALUES($id,$title,$folder,$words,$modified,$archived)";
            cmd.Parameters.AddWithValue("$id", story.Id.ToString()); cmd.Parameters.AddWithValue("$title", story.Title);
            cmd.Parameters.AddWithValue("$folder", story.Folder); cmd.Parameters.AddWithValue("$words", story.Words);
            cmd.Parameters.AddWithValue("$modified", story.Modified.ToString("O")); cmd.Parameters.AddWithValue("$archived", story.Archived);
            cmd.ExecuteNonQuery();
        }
        transaction.Commit();
    }
    public void Dispose() => connection.Dispose();
}
