using System;

using System.Data;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.IO;
using System.ServiceModel.Syndication;
using Dapper;
using Microsoft.Data.Sqlite;
using netRSS.Models;

namespace netRSS.Services;

public class FeedValidationService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbConnectionFactory _dbConnectionFactory;
    
    public FeedValidationService(IHttpClientFactory httpClientFactory, IDbConnectionFactory dbConnectionFactory)
    {
        _httpClientFactory = httpClientFactory;
        _dbConnectionFactory = dbConnectionFactory;
    }
    
    public async Task<(bool isValid, string errorMessage)> ValidateFeed(string feedUrl, int timeoutSeconds = 30)
    {
        try
        {
            // Check if this is a FeedBurner URL and handle it specially
            var actualFeedUrl = await HandleFeedBurnerUrl(feedUrl);
            if (actualFeedUrl != feedUrl)
            {
                // FeedBurner URL was detected and resolved, use the actual feed URL
                feedUrl = actualFeedUrl;
            }
            
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
            
            // Add browser-like headers to avoid 403 errors
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");
            
            // Follow redirects
            var response = await httpClient.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead);
            
            // Handle redirects manually to get the final URL
            if (response.StatusCode == System.Net.HttpStatusCode.Found || 
                response.StatusCode == System.Net.HttpStatusCode.Moved || 
                response.StatusCode == System.Net.HttpStatusCode.MovedPermanently)
            {
                var redirectUrl = response.Headers.Location;
                if (redirectUrl != null)
                {
                    response = await httpClient.GetAsync(redirectUrl);
                }
            }
            
            // Check for status code
            if (!response.IsSuccessStatusCode)
            {
                return (false, $"HTTP error: {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            
            // Get content
            var content = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrEmpty(content))
            {
                return (false, "Empty response received");
            }
            
            // Try to parse as XML
            try
            {
                // Try to parse as RSS/Atom feed
                using var reader = XmlReader.Create(new StringReader(content));
                var feed = SyndicationFeed.Load(reader);
                
                // Check if feed has items
                if (feed.Items == null || !feed.Items.Any())
                {
                    return (true, "Warning: Feed has no items"); // Still valid but with warning
                }
                
                return (true, string.Empty);
            }
            catch (XmlException)
            {
                // Try to detect RDF format
                try
                {
                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(content);
                    
                    // Check if this is an RDF feed
                    var isRdf = xmlDoc.DocumentElement?.NamespaceURI == "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
                    if (isRdf)
                    {
                        return (true, string.Empty);
                    }
                    
                    // Check if this might be an HTML page with a feed link
                    if (content.Contains("<html", StringComparison.OrdinalIgnoreCase))
                    {
                        // Look for common feed link patterns
                        var feedLinkPatterns = new[]
                        {
                            "application/rss+xml",
                            "application/atom+xml",
                            "application/xml",
                            "text/xml"
                        };
                        
                        foreach (var pattern in feedLinkPatterns)
                        {
                            if (content.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                            {
                                return (false, "This appears to be an HTML page with a feed link. Please use the feed URL directly.");
                            }
                        }
                    }
                    
                    return (false, "Not a valid RSS, Atom, or RDF feed");
                }
                catch (Exception ex)
                {
                    return (false, $"Invalid XML format: {ex.Message}");
                }
            }
        }
        catch (TaskCanceledException)
        {
            return (false, $"Connection timed out after {timeoutSeconds} seconds");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Connection error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Validation error: {ex.Message}");
        }
    }
    
    private async Task<string> HandleFeedBurnerUrl(string feedUrl)
    {
        try
        {
            // Check if this is a FeedBurner URL
            if (!IsFeedBurnerUrl(feedUrl))
            {
                return feedUrl; // Not a FeedBurner URL, return as-is
            }
            
            Console.WriteLine($"Detected FeedBurner URL: {feedUrl}");
            
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(15); // Slightly longer timeout
            
            // Add headers to mimic a feed reader - FeedBurner responds differently to different user agents
            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; FeedReader/1.0; +http://www.feedreader.com/bot)");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/rss+xml, application/atom+xml, application/xml, text/xml, */*");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            
            // Get the response with full content to handle FeedBurner properly
            var response = await httpClient.GetAsync(feedUrl);
            
            // Check for redirects and follow them
            string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? feedUrl;
            Console.WriteLine($"FeedBurner final URL after redirects: {finalUrl}");
            
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"FeedBurner URL failed with status: {response.StatusCode}");
                return feedUrl; // Return original URL if we can't access it
            }
            
            // Get the content to verify it's a feed
            var content = await response.Content.ReadAsStringAsync();
            
            // Check if the content is actually XML/feed content
            if (string.IsNullOrEmpty(content))
            {
                Console.WriteLine("FeedBurner returned empty content");
                return feedUrl;
            }
            
            // Look for feed indicators in the content
            var contentLower = content.ToLower();
            bool isFeedContent = contentLower.Contains("<rss") || 
                                contentLower.Contains("<feed") || 
                                contentLower.Contains("<rdf:rdf") ||
                                contentLower.Contains("xmlns=\"http://www.w3.org/2005/atom\"") ||
                                contentLower.Contains("xmlns:atom=\"http://www.w3.org/2005/atom\"");
            
            if (isFeedContent)
            {
                Console.WriteLine($"FeedBurner URL resolved successfully to: {finalUrl}");
                return finalUrl;
            }
            else
            {
                // If we got HTML content, try to extract the actual feed URL from it
                var actualFeedUrl = ExtractFeedUrlFromHtml(content, finalUrl);
                if (actualFeedUrl != null && actualFeedUrl != feedUrl)
                {
                    Console.WriteLine($"Extracted feed URL from FeedBurner HTML: {actualFeedUrl}");
                    return actualFeedUrl;
                }
                
                Console.WriteLine("FeedBurner URL did not resolve to feed content");
                return feedUrl;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling FeedBurner URL {feedUrl}: {ex.Message}");
            return feedUrl; // Return original URL on error
        }
    }
    
    private string? ExtractFeedUrlFromHtml(string htmlContent, string baseUrl)
    {
        try
        {
            // Look for common patterns in FeedBurner HTML pages that contain the actual feed URL
            var patterns = new[]
            {
                @"<link[^>]+type=['""]application/rss\+xml['""][^>]+href=['""]([^'""]+)['""]",
                @"<link[^>]+href=['""]([^'""]+)['""][^>]+type=['""]application/rss\+xml['""]",
                @"<link[^>]+type=['""]application/atom\+xml['""][^>]+href=['""]([^'""]+)['""]",
                @"<link[^>]+href=['""]([^'""]+)['""][^>]+type=['""]application/atom\+xml['""]",
                @"<link[^>]+type=['""]application/xml['""][^>]+href=['""]([^'""]+)['""]",
                @"<link[^>]+href=['""]([^'""]+)['""][^>]+type=['""]application/xml['""]"
            };
            
            foreach (var pattern in patterns)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(htmlContent, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (match.Groups.Count > 1)
                    {
                        var url = match.Groups[1].Value;
                        if (!string.IsNullOrEmpty(url))
                        {
                            // Convert relative URLs to absolute
                            if (url.StartsWith("//"))
                            {
                                url = "https:" + url;
                            }
                            else if (url.StartsWith("/"))
                            {
                                var baseUri = new Uri(baseUrl);
                                url = $"{baseUri.Scheme}://{baseUri.Host}{url}";
                            }
                            else if (!url.StartsWith("http"))
                            {
                                var baseUri = new Uri(baseUrl);
                                url = new Uri(baseUri, url).ToString();
                            }
                            
                            return url;
                        }
                    }
                }
            }
            
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error extracting feed URL from HTML: {ex.Message}");
            return null;
        }
    }
    
    private static bool IsFeedBurnerUrl(string url)
    {
        if (string.IsNullOrEmpty(url))
            return false;
            
        try
        {
            var uri = new Uri(url.ToLower());
            
            // Common FeedBurner domains and patterns
            var feedBurnerDomains = new[]
            {
                "feeds.feedburner.com",
                "feedburner.google.com",
                "feeds2.feedburner.com",
                "feedproxy.google.com",
                "feeds.feedburner.com",
                "www.feedburner.com"
            };
            
            // Check for FeedBurner domains
            if (feedBurnerDomains.Any(domain => uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            
            // Check for FeedBurner path patterns
            if (uri.Host.Contains("feedburner") || uri.AbsoluteUri.Contains("feedburner"))
            {
                return true;
            }
            
            return false;
        }
        catch (UriFormatException)
        {
            // If URL is malformed, it's not a FeedBurner URL
            return false;
        }
    }
    
    public async Task UpdateFeedStatus(int feedId, string feedUrl, string feedName)
    {
        try
        {
            using var connection = _dbConnectionFactory.CreateConnection();
            connection.Open();
            
            // First check if the feed still exists
            var feedExists = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM feeds WHERE id = @FeedId",
                new { FeedId = feedId }
            );
            
            if (feedExists == 0)
            {
                // Feed doesn't exist anymore, nothing to update
                return;
            }
            
            var (isValid, errorMessage) = await ValidateFeed(feedUrl);
            
            await UpdateFeedStatusInternal(feedId, isValid, errorMessage, connection);
        }
        catch (Exception ex)
        {
            // Log the error but don't crash the validation process
            Console.WriteLine($"Error updating feed status for feed {feedId} ({feedName}): {ex.Message}");
        }
    }
    
    public async Task UpdateFeedStatus(int feedId, string feedUrl, string feedName, bool isValid, string errorMessage)
    {
        try
        {
            using var connection = _dbConnectionFactory.CreateConnection();
            connection.Open();
            
            // First check if the feed still exists
            var feedExists = connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM feeds WHERE id = @FeedId",
                new { FeedId = feedId }
            );
            
            if (feedExists == 0)
            {
                // Feed doesn't exist anymore, nothing to update
                return;
            }
            
            await UpdateFeedStatusInternal(feedId, isValid, errorMessage, connection);
        }
        catch (Exception ex)
        {
            // Log the error but don't crash the validation process
            Console.WriteLine($"Error updating feed status for feed {feedId} ({feedName}): {ex.Message}");
        }
    }
    
    private async Task UpdateFeedStatusInternal(int feedId, bool isValid, string errorMessage, IDbConnection connection)
    {
        // Get current status
        var existingStatus = connection.QueryFirstOrDefault<FeedStatus>(
            "SELECT * FROM feed_status WHERE feed_id = @FeedId",
            new { FeedId = feedId }
        );
        
        if (existingStatus == null)
        {
            // Create new status entry
            if (isValid)
            {
                connection.Execute(
                    "INSERT INTO feed_status (feed_id, status, error_message, last_checked) VALUES (@FeedId, 'ok', '', @LastChecked)",
                    new { FeedId = feedId, LastChecked = DateTime.Now }
                );
            }
            else
            {
                connection.Execute(
                    "INSERT INTO feed_status (feed_id, status, error_message, last_checked, fail_count, is_critical) " +
                    "VALUES (@FeedId, 'error', @ErrorMessage, @LastChecked, 1, 1)",
                    new { FeedId = feedId, ErrorMessage = errorMessage, LastChecked = DateTime.Now }
                );
            }
        }
        else
        {
            // Update existing status
            if (isValid)
            {
                connection.Execute(
                    "UPDATE feed_status SET status = 'ok', error_message = '', last_checked = @LastChecked, fail_count = 0, is_critical = 0 WHERE feed_id = @FeedId",
                    new { FeedId = feedId, LastChecked = DateTime.Now }
                );
            }
            else
            {
                // Increment fail count
                int failCount = existingStatus.FailCount + 1;
                bool isCritical = failCount >= 3 || errorMessage.Contains("404") || errorMessage.Contains("403");
                
                connection.Execute(
                    "UPDATE feed_status SET status = 'error', error_message = @ErrorMessage, last_checked = @LastChecked, " +
                    "fail_count = @FailCount, is_critical = @IsCritical WHERE feed_id = @FeedId",
                    new { 
                        FeedId = feedId, 
                        ErrorMessage = errorMessage, 
                        LastChecked = DateTime.Now,
                        FailCount = failCount,
                        IsCritical = isCritical ? 1 : 0
                    }
                );
            }
        }
    }
    
    public async Task ValidateAllFeeds()
    {
        try
        {
            using var connection = _dbConnectionFactory.CreateConnection();
            connection.Open();
            
            var feeds = connection.Query<Feed>("SELECT * FROM feeds").ToList();
            Console.WriteLine($"Validating {feeds.Count} feeds");
            
            foreach (var feed in feeds)
            {
                try
                {
                    await UpdateFeedStatus(feed.id, feed.url, feed.name);
                    await Task.Delay(200); // Small delay to prevent throttling
                }
                catch (Exception ex)
                {
                    // Log the error but continue with other feeds
                    Console.WriteLine($"Error validating feed {feed.name} ({feed.id}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error during feed validation: {ex.Message}");
        }
    }
    
    public async Task<string> ResolveFeedBurnerUrl(string feedUrl)
    {
        return await HandleFeedBurnerUrl(feedUrl);
    }
} 