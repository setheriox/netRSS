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

    // Create the database file if it doesn't exist
    if (!File.Exists(dbPath))
    {
        // Create an empty database file
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Console.WriteLine($"Created new database file at {dbPath}");
    }

    // Check if the tables exist and create them if they don't
    using var dbConnection = new SqliteConnection($"Data Source={dbPath}");
    dbConnection.Open();

    // Read the SQL schema from the rss.sql file
    string sqlFilePath = Path.Combine(Directory.GetCurrentDirectory(), "Data", "rss.sql");
    
    if (!File.Exists(sqlFilePath))
    {
        Console.WriteLine($"ERROR: SQL schema file not found at {sqlFilePath}");
        throw new FileNotFoundException($"SQL schema file not found at {sqlFilePath}");
    }
    
    string sqlSchema = File.ReadAllText(sqlFilePath);
    
    // Check if the entries table exists
    try
    {
        dbConnection.ExecuteScalar<int>("SELECT COUNT(*) FROM entries");
        Console.WriteLine("Database schema already exists");
    }
    catch (Exception)
    {
        // If the query fails, the table doesn't exist, so create the schema
        dbConnection.Execute(sqlSchema);
        Console.WriteLine($"Database schema created using {sqlFilePath}");
    }

    // Check if starred column exists in entries table
    try
    {
        // Try to select the starred column - this will fail if it doesn't exist
        dbConnection.ExecuteScalar<int>("SELECT starred FROM entries LIMIT 1");
    }
    catch (Exception)
    {
        // If the query fails, the column doesn't exist, so add it
        dbConnection.Execute("ALTER TABLE entries ADD COLUMN starred INTEGER DEFAULT 0");
        Console.WriteLine("Added starred column to entries table");
    }
    
    // Check if manually_filtered column exists in entries table
    try
    {
        // Try to select the manually_filtered column - this will fail if it doesn't exist
        dbConnection.ExecuteScalar<int>("SELECT manually_filtered FROM entries LIMIT 1");
    }
    catch (Exception)
    {
        // If the query fails, the column doesn't exist, so add it
        dbConnection.Execute("ALTER TABLE entries ADD COLUMN manually_filtered INTEGER DEFAULT 0");
        Console.WriteLine("Added manually_filtered column to entries table");
    }
    
    // Check if filter_reason column exists in entries table
    try
    {
        // Try to select the filter_reason column - this will fail if it doesn't exist
        dbConnection.ExecuteScalar<string>("SELECT filter_reason FROM entries LIMIT 1");
    }
    catch (Exception)
    {
        // If the query fails, the column doesn't exist, so add it
        dbConnection.Execute("ALTER TABLE entries ADD COLUMN filter_reason TEXT DEFAULT NULL");
        Console.WriteLine("Added filter_reason column to entries table");
    }
    
    // Check if settings table exists
    try
    {
        // Try to select from settings table - this will fail if it doesn't exist
        dbConnection.ExecuteScalar<string>("SELECT key FROM settings LIMIT 1");
    }
    catch (Exception)
    {
        // If the query fails, the table doesn't exist, so create it
        dbConnection.Execute(@"
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
        dbConnection.ExecuteScalar<string>("SELECT status FROM feed_status LIMIT 1");
    }
    catch (Exception)
    {
        // If the query fails, the table doesn't exist, so create it
        dbConnection.Execute(@"
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
        dbConnection.Execute(@"
            INSERT INTO feed_status (feed_id, status)
            SELECT id, 'ok' FROM feeds 
            WHERE id NOT IN (SELECT feed_id FROM feed_status)
        ");
    }

    // Check if display_term column exists in filters table
    try
    {
        // Try to select the display_term column - this will fail if it doesn't exist
        dbConnection.ExecuteScalar<string>("SELECT display_term FROM filters LIMIT 1");
    }
    catch (Exception)
    {
        // If the query fails, the column doesn't exist, so add it
        dbConnection.Execute("ALTER TABLE filters ADD COLUMN display_term TEXT");
        
        // Update existing filters to set display_term to the user-friendly version of term
        dbConnection.Execute(@"
            UPDATE filters 
            SET display_term = 
                CASE 
                    WHEN term LIKE '%\%%' THEN TRIM(term, '%')
                    ELSE term
                END
        ");
        Console.WriteLine("Added display_term column to filters table");
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
