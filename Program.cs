using Microsoft.Data.Sqlite;
using netRSS.Components;
using Dapper;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Add HttpClient services
builder.Services.AddHttpClient();

// Configure SQLite
string dbPath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "rss.db");
builder.Services.AddSingleton<SqliteConnection>(sp => new SqliteConnection($"Data Source={dbPath}"));

// Register DbConnectionFactory to create db connections
builder.Services.AddSingleton<IDbConnectionFactory>(sp => 
{
    return new SqliteConnectionFactory($"Data Source={dbPath}");
});

// Register the RSS Background Service
builder.Services.AddHostedService<RssBackgroundService>();

// Configure Kestrel to listen on all interfaces
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.ListenAnyIP(15001); // Listen on port 15000 on all interfaces
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    // app.UseHsts();
}

// Remove HTTPS redirection
// app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Ensure the database exists
EnsureDatabaseExists(dbPath);

// Add test data if needed
InsertTestData(dbPath);

app.Run();

// Helper methods
void EnsureDatabaseExists(string dbPath)
{
    // Ensure the Data directory exists
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    // If the database doesn't exist, create it and add the schema
    if (!File.Exists(dbPath))
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        // Create tables based on rss.sql
        connection.Execute(@"
            CREATE TABLE categories (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                color TEXT NOT NULL
            );

            CREATE TABLE feeds (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                url TEXT NOT NULL,
                category_id INTEGER NOT NULL,
                FOREIGN KEY (category_id) REFERENCES categories (id)
            );

            CREATE TABLE entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                title TEXT NOT NULL,
                description TEXT,
                link TEXT,
                published DATETIME NOT NULL,
                feed_id INTEGER NOT NULL, 
                read INTEGER DEFAULT 0,
                filtered INTEGER DEFAULT 0,
                starred INTEGER DEFAULT 0,
                FOREIGN KEY (feed_id) REFERENCES feeds (id)
            );

            CREATE TABLE filters (
                id INTEGER PRIMARY KEY, 
                term TEXT NOT NULL, 
                title BOOLEAN DEFAULT 0,
                description BOOLEAN DEFAULT 0,
                UNIQUE(term)
            );

            CREATE TABLE allowlist (
                entry_id INTEGER PRIMARY KEY,
                FOREIGN KEY (entry_id) REFERENCES entries (id) ON DELETE CASCADE
            );
        ");
    }
    else
    {
        // Check if starred column exists in entries table
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        try
        {
            // Try to select the starred column - this will fail if it doesn't exist
            connection.ExecuteScalar<int>("SELECT starred FROM entries LIMIT 1");
        }
        catch (Exception)
        {
            // If the query fails, the column doesn't exist, so add it
            connection.Execute("ALTER TABLE entries ADD COLUMN starred INTEGER DEFAULT 0");
            Console.WriteLine("Added starred column to entries table");
        }
    }
}

void InsertTestData(string dbPath)
{
    using var connection = new SqliteConnection($"Data Source={dbPath}");
    connection.Open();
    
    // Check if we already have categories
    int categoryCount = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM categories");
    
    if (categoryCount == 0)
    {
       
    }
}

// DbConnectionFactory implementation
public interface IDbConnectionFactory
{
    SqliteConnection CreateConnection();
}

public class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(_connectionString);
    }
}
