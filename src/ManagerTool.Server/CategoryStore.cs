using Microsoft.Data.Sqlite;

namespace ManagerTool.Server;

/// <summary>A classification rule: process/URL substring → productive / unproductive / neutral.</summary>
public sealed record Category(long Id, string Name, string Classification, string Kind, string Pattern);

public sealed record CategoryInput(string Name, string Classification, string Kind, string Pattern);

/// <summary>Classification values.</summary>
public static class Classifications
{
    public const string Productive = "productive";
    public const string Unproductive = "unproductive";
    public const string Neutral = "neutral";
}

/// <summary>
/// Classifies activity by app/website into productive / unproductive / neutral, driving the
/// productivity view in reports. Substring match, first hit wins; unmatched = neutral. Seeds a
/// trading-desk-oriented default set on first init.
/// </summary>
public sealed class CategoryStore
{
    private readonly string _connectionString;

    public CategoryStore(string dbPath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        Initialize();
    }

    private SqliteConnection Open() { var c = new SqliteConnection(_connectionString); c.Open(); return c; }

    private void Initialize()
    {
        using var conn = Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS categories (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    name           TEXT NOT NULL,
                    classification TEXT NOT NULL,
                    kind           TEXT NOT NULL,   -- 'app' | 'url'
                    pattern        TEXT NOT NULL
                );
                """;
            cmd.ExecuteNonQuery();
        }
        SeedIfEmpty(conn);
    }

    private static void SeedIfEmpty(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM categories;";
            if (Convert.ToInt64(check.ExecuteScalar()) > 0) return;
        }
        (string name, string cls, string kind, string pattern)[] defaults =
        {
            // Productive — trading tools + office
            ("Trading terminal", Classifications.Productive, "app", "terminal"),
            ("Excel", Classifications.Productive, "app", "excel"),
            ("NSE / exchange site", Classifications.Productive, "url", "nseindia.com"),
            ("BSE site", Classifications.Productive, "url", "bseindia.com"),
            ("Broker/algo site", Classifications.Productive, "url", "kite.zerodha.com"),
            // Unproductive — social / streaming / shopping / personal
            ("YouTube", Classifications.Unproductive, "url", "youtube.com"),
            ("Facebook", Classifications.Unproductive, "url", "facebook.com"),
            ("Instagram", Classifications.Unproductive, "url", "instagram.com"),
            ("WhatsApp Web", Classifications.Unproductive, "url", "web.whatsapp.com"),
            ("Amazon shopping", Classifications.Unproductive, "url", "amazon.in"),
            ("Netflix", Classifications.Unproductive, "url", "netflix.com"),
            ("Personal webmail", Classifications.Unproductive, "url", "mail.google.com"),
        };
        foreach (var (name, cls, kind, pattern) in defaults)
        {
            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO categories (name, classification, kind, pattern) VALUES ($n,$c,$k,$p);";
            ins.Parameters.AddWithValue("$n", name);
            ins.Parameters.AddWithValue("$c", cls);
            ins.Parameters.AddWithValue("$k", kind);
            ins.Parameters.AddWithValue("$p", pattern);
            ins.ExecuteNonQuery();
        }
    }

    private volatile List<Category>? _cache;
    private List<Category> Cache => _cache ??= LoadAll();

    private List<Category> LoadAll()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, classification, kind, pattern FROM categories ORDER BY id;";
        var list = new List<Category>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Category(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    public IReadOnlyList<Category> GetAll() => Cache;

    /// <summary>Classify a process/URL. First matching category wins; default neutral.</summary>
    public string Classify(string processName, string? url)
    {
        foreach (var c in Cache)
        {
            var haystack = c.Kind == "url" ? url : processName;
            if (haystack is not null && haystack.Contains(c.Pattern, StringComparison.OrdinalIgnoreCase))
                return c.Classification;
        }
        return Classifications.Neutral;
    }

    public Category Create(CategoryInput input)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO categories (name, classification, kind, pattern) VALUES ($n,$c,$k,$p); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", input.Name);
        cmd.Parameters.AddWithValue("$c", input.Classification);
        cmd.Parameters.AddWithValue("$k", input.Kind);
        cmd.Parameters.AddWithValue("$p", input.Pattern);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        _cache = null;
        return new Category(id, input.Name, input.Classification, input.Kind, input.Pattern);
    }

    public bool Delete(long id)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM categories WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id);
        var ok = cmd.ExecuteNonQuery() > 0;
        _cache = null;
        return ok;
    }
}
