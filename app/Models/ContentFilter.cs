namespace netRSS.Models {
    public class ContentFilter {
        public int Id { get; set; }
        public string Term { get; set; } = string.Empty;
        public bool Title { get; set; }
        public bool Description { get; set; }
        public int MatchCount { get; set; }
        public string DisplayTerm { get; set; } = string.Empty;

        public string FilterType => 
            (Title && Description) ? "Both" :
            Title ? "Title" :
            Description ? "Body" : "None";
    }
} 