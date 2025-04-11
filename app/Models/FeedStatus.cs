namespace netRSS.Models;

public class FeedStatus
{
    public int Id { get; set; }
    public int FeedId { get; set; }
    public string FeedName { get; set; } = string.Empty;
    public string FeedUrl { get; set; } = string.Empty;
    public string Status { get; set; } = "ok"; // ok, error, timeout, not_found
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTime LastChecked { get; set; } = DateTime.Now;
    public int FailCount { get; set; } = 0;
    public bool IsCritical { get; set; } = false;
} 