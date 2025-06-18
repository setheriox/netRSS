namespace netRSS.Services;

public class DatabasePathProvider(string dbPath)
{
    public string Path { get; } = dbPath;
}
 