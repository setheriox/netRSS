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

// Register feed validation service
builder.Services.AddScoped<netRSS.Services.FeedValidationService>();

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

            CREATE TABLE feed_status (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                feed_id INTEGER NOT NULL,
                status TEXT DEFAULT 'ok',
                error_message TEXT,
                last_checked DATETIME DEFAULT CURRENT_TIMESTAMP,
                fail_count INTEGER DEFAULT 0,
                is_critical BOOLEAN DEFAULT 0,
                FOREIGN KEY (feed_id) REFERENCES feeds (id) ON DELETE CASCADE
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
            
            CREATE TABLE settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL,
                type TEXT DEFAULT 'string',
                description TEXT
            );
            
            -- Insert default settings
            INSERT INTO settings (key, value, type, description) 
            VALUES ('font_family', 'Verdana, sans-serif', 'string', 'Font family for the application');
            
            INSERT INTO settings (key, value, type, description) 
            VALUES ('font_size', '8', 'number', 'Base font size in points');
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
        
        // Check if manually_filtered column exists in entries table
        try
        {
            // Try to select the manually_filtered column - this will fail if it doesn't exist
            connection.ExecuteScalar<int>("SELECT manually_filtered FROM entries LIMIT 1");
        }
        catch (Exception)
        {
            // If the query fails, the column doesn't exist, so add it
            connection.Execute("ALTER TABLE entries ADD COLUMN manually_filtered INTEGER DEFAULT 0");
            Console.WriteLine("Added manually_filtered column to entries table");
        }
        
        // Check if filter_reason column exists in entries table
        try
        {
            // Try to select the filter_reason column - this will fail if it doesn't exist
            connection.ExecuteScalar<string>("SELECT filter_reason FROM entries LIMIT 1");
        }
        catch (Exception)
        {
            // If the query fails, the column doesn't exist, so add it
            connection.Execute("ALTER TABLE entries ADD COLUMN filter_reason TEXT DEFAULT NULL");
            Console.WriteLine("Added filter_reason column to entries table");
        }
        
        // Check if settings table exists
        try
        {
            // Try to select from settings table - this will fail if it doesn't exist
            connection.ExecuteScalar<string>("SELECT key FROM settings LIMIT 1");
        }
        catch (Exception)
        {
            // If the query fails, the table doesn't exist, so create it
            connection.Execute(@"
                CREATE TABLE settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL,
                    type TEXT DEFAULT 'string',
                    description TEXT
                );
                
                -- Insert default settings
                INSERT INTO settings (key, value, type, description) 
                VALUES ('font_family', 'Verdana, sans-serif', 'string', 'Font family for the application');
                
                INSERT INTO settings (key, value, type, description) 
                VALUES ('font_size', '8', 'number', 'Base font size in points');
            ");
            Console.WriteLine("Created settings table with default values");
        }

        // Check if feed_status table exists
        try 
        {
            // Try to select from feed_status table - this will fail if it doesn't exist
            connection.ExecuteScalar<string>("SELECT status FROM feed_status LIMIT 1");
        }
        catch (Exception)
        {
            // If the query fails, the table doesn't exist, so create it
            connection.Execute(@"
                CREATE TABLE feed_status (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    feed_id INTEGER NOT NULL,
                    status TEXT DEFAULT 'ok',
                    error_message TEXT,
                    last_checked DATETIME DEFAULT CURRENT_TIMESTAMP,
                    fail_count INTEGER DEFAULT 0,
                    is_critical BOOLEAN DEFAULT 0,
                    FOREIGN KEY (feed_id) REFERENCES feeds (id) ON DELETE CASCADE
                );
            ");
            Console.WriteLine("Created feed_status table");
            
            // Initialize status for all existing feeds
            connection.Execute(@"
                INSERT INTO feed_status (feed_id, status)
                SELECT id, 'ok' FROM feeds 
                WHERE id NOT IN (SELECT feed_id FROM feed_status)
            ");
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
