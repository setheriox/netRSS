using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Collections.Generic;
using System.Xml;
using System.Diagnostics;
using System.Data;
using System.Linq;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using netRSS.Models;
using netRSS.Services;
using System.ServiceModel.Syndication;
using System.IO;

public class RssBackgroundService : BackgroundService
{
    private readonly ILogger<RssBackgroundService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbConnectionFactory _dbConnectionFactory;
    private readonly FeedValidationService _feedValidationService;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(10);

    public RssBackgroundService(
        ILogger<RssBackgroundService> logger,
        IHttpClientFactory httpClientFactory,
        IDbConnectionFactory dbConnectionFactory,
        FeedValidationService feedValidationService)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _dbConnectionFactory = dbConnectionFactory;
        _feedValidationService = feedValidationService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] RSS Background Service is starting.");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshFeeds();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error while refreshing feeds.");
            }

            _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Waiting for next refresh...");
            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task RefreshFeeds()
    {
        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Starting RSS feed refresh...");
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var httpClient = _httpClientFactory.CreateClient();
            // Add browser-like headers to avoid 403 errors
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");

            using var connection = _dbConnectionFactory.CreateConnection();
            connection.Open();

            // Clean up any orphaned feed status records
            try
            {
                var orphanedDeleted = connection.Execute(@"
                    DELETE FROM feed_status 
                    WHERE feed_id NOT IN (SELECT id FROM feeds)");
                
                if (orphanedDeleted > 0)
                {
                    _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Cleaned up {orphanedDeleted} orphaned feed status records");
                }
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error cleaning up orphaned feed status: {cleanupEx.Message}");
            }

            // Get all filters at the start
            var filters = connection.Query<Filter>("SELECT * FROM filters").ToList();
            _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Loaded {filters.Count} filters");

            var feeds = connection.Query<Feed>("SELECT * FROM feeds").ToList();
            _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Found {feeds.Count} feeds to refresh.");

            foreach (var feed in feeds)
            {
                try
                {
                    _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Fetching feed: {feed.name} ({feed.url})");
                    
                    // Resolve FeedBurner URLs before making the request
                    string actualFeedUrl = await _feedValidationService.ResolveFeedBurnerUrl(feed.url);
                    if (actualFeedUrl != feed.url)
                    {
                        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] FeedBurner URL resolved from {feed.url} to {actualFeedUrl}");
                    }
                    
                    // Add retry logic for failed requests
                    HttpResponseMessage? response = null;
                    string? content = null;
                    int maxRetries = 3;
                    int currentTry = 0;
                    
                    while (currentTry < maxRetries)
                    {
                        try
                        {
                            response = await httpClient.GetAsync(actualFeedUrl);
                            if (response.IsSuccessStatusCode)
                            {
                                content = await response.Content.ReadAsStringAsync();
                                break;
                            }
                            currentTry++;
                            if (currentTry < maxRetries)
                            {
                                await Task.Delay(1000 * currentTry); // Exponential backoff
                            }
                        }
                        catch (Exception)
                        {
                            currentTry++;
                            if (currentTry >= maxRetries) throw;
                            await Task.Delay(1000 * currentTry);
                        }
                    }
                    
                    if (content == null)
                    {
                        throw new Exception($"Failed to fetch feed after {maxRetries} attempts. Status: {response?.StatusCode}");
                    }
                    
                    _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Downloaded {content.Length} bytes from {actualFeedUrl}");
                    
                    // Clean up the XML content to handle malformed DOCTYPE declarations
                    content = CleanXmlContent(content);
                    
                    var xmlDoc = new XmlDocument();
                    xmlDoc.LoadXml(content);

                    var isRdf = xmlDoc.DocumentElement?.NamespaceURI == "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
                    if (isRdf)
                    {
                        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Detected RDF feed format for {feed.name}");
                        var nsmgr = new XmlNamespaceManager(xmlDoc.NameTable);
                        nsmgr.AddNamespace("rdf", "http://www.w3.org/1999/02/22-rdf-syntax-ns#");
                        nsmgr.AddNamespace("rss", "http://purl.org/rss/1.0/");
                        nsmgr.AddNamespace("dc", "http://purl.org/dc/elements/1.1/");

                        var items = xmlDoc.SelectNodes("//rss:item", nsmgr);
                        if (items != null)
                        {
                            _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Found {items.Count} items in RDF feed");
                            foreach (XmlNode item in items)
                            {
                                var title = item.SelectSingleNode("rss:title", nsmgr)?.InnerText ?? "";
                                var description = item.SelectSingleNode("rss:description", nsmgr)?.InnerText ?? "";
                                var link = item.SelectSingleNode("rss:link", nsmgr)?.InnerText ?? "";
                                var dateStr = item.SelectSingleNode("dc:date", nsmgr)?.InnerText;

                                DateTime published;
                                if (!DateTime.TryParse(dateStr, out published))
                                {
                                    published = DateTime.Now;
                                }

                                var existingEntry = connection.QueryFirstOrDefault<Entry>(
                                    "SELECT * FROM entries WHERE link = @Link AND feed_id = @FeedId",
                                    new { Link = link, FeedId = feed.id }
                                );

                                if (existingEntry == null)
                                {
                                    // Insert the entry first
                                    connection.Execute(@"
                                        INSERT INTO entries (title, description, link, published, feed_id, read, filtered)
                                        VALUES (@Title, @Description, @Link, @Published, @FeedId, 0, 0)",
                                        new {
                                            Title = title,
                                            Description = description,
                                            Link = link,
                                            Published = published,
                                            FeedId = feed.id
                                        }
                                    );

                                    // Then apply filters using SQL
                                    if (filters.Any())
                                    {
                                        string applySql = @"
                                            UPDATE entries
                                            SET filtered = 1
                                            WHERE link = @Link 
                                            AND EXISTS (
                                                SELECT 1 FROM filters f
                                                WHERE (f.title = 1 AND entries.title LIKE '%' || f.term || '%')
                                                   OR (f.description = 1 AND entries.description LIKE '%' || f.term || '%')
                                            )";
                                        
                                        int affectedRows = connection.Execute(applySql, new { Link = link });
                                        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Added new entry: {title} (Filtered: {affectedRows > 0})");
                                    }
                                    else
                                    {
                                        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Added new entry: {title}");
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        var reader = XmlReader.Create(new StringReader(content));
                        var syndicationFeed = SyndicationFeed.Load(reader);
                        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Parsed feed: {feed.name} with {syndicationFeed.Items.Count()} items");

                        foreach (var item in syndicationFeed.Items)
                        {
                            var link = item.Links.FirstOrDefault()?.Uri.ToString() ?? "";
                            var existingEntry = connection.QueryFirstOrDefault<Entry>(
                                "SELECT * FROM entries WHERE link = @Link AND feed_id = @FeedId",
                                new { Link = link, FeedId = feed.id }
                            );

                            if (existingEntry == null)
                            {
                                // Insert the entry first
                                connection.Execute(@"
                                    INSERT INTO entries (title, description, link, published, feed_id, read, filtered)
                                    VALUES (@Title, @Description, @Link, @Published, @FeedId, 0, 0)",
                                    new {
                                        Title = item.Title.Text,
                                        Description = item.Summary?.Text ?? "",
                                        Link = link,
                                        Published = item.PublishDate.DateTime,
                                        FeedId = feed.id
                                    }
                                );

                                // Then apply filters using SQL
                                if (filters.Any())
                                {
                                    string applySql = @"
                                        UPDATE entries
                                        SET filtered = 1
                                        WHERE link = @Link 
                                        AND EXISTS (
                                            SELECT 1 FROM filters f
                                            WHERE (f.title = 1 AND entries.title LIKE '%' || f.term || '%')
                                               OR (f.description = 1 AND entries.description LIKE '%' || f.term || '%')
                                        )";
                                    
                                    int affectedRows = connection.Execute(applySql, new { Link = link });
                                    _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Added new entry: {item.Title.Text} (Filtered: {affectedRows > 0})");
                                }
                                else
                                {
                                    _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Added new entry: {item.Title.Text}");
                                }
                            }
                        }
                    }

                    _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Successfully processed feed: {feed.name}");
                }
                catch (Exception ex)
                {
                    // Log the error with more detail
                    string errorDetail = ex.Message;
                    
                    // Special handling for foreign key errors
                    if (ex.Message.Contains("FOREIGN KEY constraint failed"))
                    {
                        errorDetail = "Foreign key constraint error. This may occur if the feed was deleted while processing entries.";
                        
                        // Try to clean up any orphaned entries
                        try 
                        {
                            connection.Execute("DELETE FROM entries WHERE feed_id NOT IN (SELECT id FROM feeds)");
                        }
                        catch {}
                    }
                    
                    _logger.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error processing feed {feed.name}: {errorDetail}");
                    
                    if (ex.StackTrace != null)
                    {
                        _logger.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Stack trace: {ex.StackTrace}");
                    }
                    
                    // Update the feed status to mark it as problematic
                    try
                    {
                        bool isConnectionError = ex.Message.Contains("connection") || 
                                               ex.Message.Contains("timeout") || 
                                               ex.Message.Contains("404") ||
                                               ex.Message.Contains("403");
                        
                        var existingStatus = connection.QueryFirstOrDefault<FeedStatus>(
                            "SELECT * FROM feed_status WHERE feed_id = @FeedId", 
                            new { FeedId = feed.id }
                        );
                        
                        if (existingStatus != null)
                        {
                            int failCount = existingStatus.FailCount + 1;
                            bool isCritical = failCount >= 3 || isConnectionError;
                            
                            connection.Execute(
                                "UPDATE feed_status SET status = 'error', error_message = @ErrorMessage, " +
                                "last_checked = @LastChecked, fail_count = @FailCount, is_critical = @IsCritical " +
                                "WHERE feed_id = @FeedId",
                                new { 
                                    FeedId = feed.id, 
                                    ErrorMessage = errorDetail.Substring(0, Math.Min(errorDetail.Length, 1000)), 
                                    LastChecked = DateTime.Now,
                                    FailCount = failCount,
                                    IsCritical = isCritical ? 1 : 0
                                }
                            );
                        }
                        else
                        {
                            connection.Execute(
                                "INSERT INTO feed_status (feed_id, status, error_message, last_checked, fail_count, is_critical) " +
                                "VALUES (@FeedId, 'error', @ErrorMessage, @LastChecked, 1, @IsCritical)",
                                new { 
                                    FeedId = feed.id, 
                                    ErrorMessage = errorDetail.Substring(0, Math.Min(errorDetail.Length, 1000)), 
                                    LastChecked = DateTime.Now,
                                    IsCritical = isConnectionError ? 1 : 0
                                }
                            );
                        }
                    }
                    catch (Exception updateEx)
                    {
                        _logger.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error updating feed status: {updateEx.Message}");
                    }
                }
            }

            stopwatch.Stop();
            _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Feed refresh completed in {stopwatch.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error processing feeds: {ex.Message}");
            _logger.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Stack trace: {ex.StackTrace}");
        }
    }

    private string CleanXmlContent(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        try
        {
            // Handle common malformed DOCTYPE issues
            // Replace malformed 'doctype' with proper 'DOCTYPE'
            content = System.Text.RegularExpressions.Regex.Replace(
                content, 
                @"<\s*doctype\s+", 
                "<!DOCTYPE ", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            // Remove problematic DOCTYPE declarations entirely if they're still malformed
            content = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"<!\s*DOCTYPE[^>]*>",
                "",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline
            );

            // Remove XML processing instructions that might be malformed
            content = System.Text.RegularExpressions.Regex.Replace(
                content,
                @"<\?\s*xml[^>]*\?>",
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );

            // Ensure the content starts with proper XML declaration
            if (!content.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            {
                content = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + content.TrimStart();
            }

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error cleaning XML content: {ex.Message}");
            return content; // Return original content if cleaning fails
        }
    }
}
