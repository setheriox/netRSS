using System;
using System.Net.Http;
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
        catch (Exception ex)
        {
            // Log the error but don't crash the validation process
            Console.WriteLine($"Error updating feed status for feed {feedId} ({feedName}): {ex.Message}");
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
} 