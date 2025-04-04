namespace netRSS.Models;

using System.Text.Json.Serialization;

public class Entry
{
    public int id { get; set; }
    public string title { get; set; } = string.Empty;
    public string? description { get; set; }
    public string? link { get; set; }
    public DateTime published { get; set; }
    public int feed_id { get; set; }
    public int read { get; set; } = 0;
    public int starred { get; set; } = 0;
    
    [JsonIgnore]
    public Feed? Feed { get; set; }
    
    // Properties to store joined data
    public string feed_name { get; set; } = string.Empty;
    public string feed_color { get; set; } = string.Empty;
} 