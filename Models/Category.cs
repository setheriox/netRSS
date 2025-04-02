namespace netRSS.Models;

using System.Text.Json.Serialization;

public class Category
{
    public int id { get; set; }
    public string name { get; set; } = string.Empty;
    public string color { get; set; } = string.Empty;
    
    [JsonIgnore]
    public List<Feed> Feeds { get; set; } = new List<Feed>();

    // Count properties
    public int UnreadCount { get; set; }
    public int FeedCount { get; set; }
}