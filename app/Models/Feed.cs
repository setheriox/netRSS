namespace netRSS.Models;

using System.Text.Json.Serialization;

public class Feed
{
    public int id { get; set; }
    public string name { get; set; } = string.Empty;
    public string url { get; set; } = string.Empty;
    public int category_id { get; set; }
    
    [JsonIgnore]
    public Category? Category { get; set; }
    
    [JsonIgnore]
    public List<Entry> Entries { get; set; } = new List<Entry>();
    
    // Count properties
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
} 