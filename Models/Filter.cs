namespace netRSS.Models
{
    public class Filter
    {
        public int Id { get; set; }
        public string Term { get; set; } = string.Empty;
        public bool Title { get; set; }
        public bool Description { get; set; }
    }
} 