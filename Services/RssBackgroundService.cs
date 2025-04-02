using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Collections.Generic;
using System.Xml;
using System.Diagnostics;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using netRSS.Models;
using System.ServiceModel.Syndication;

public class RssBackgroundService : BackgroundService
{
    private readonly ILogger<RssBackgroundService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDbConnectionFactory _dbConnectionFactory;
    private readonly TimeSpan _interval = TimeSpan.FromMinutes(10);

    public RssBackgroundService(
        ILogger<RssBackgroundService> logger,
        IHttpClientFactory httpClientFactory,
        IDbConnectionFactory dbConnectionFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _dbConnectionFactory = dbConnectionFactory;
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

        using var httpClient = _httpClientFactory.CreateClient();
        // Add browser-like headers to avoid 403 errors
        httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        httpClient.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        httpClient.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.5");

        using var connection = _dbConnectionFactory.CreateConnection();
        connection.Open();

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
                
                // Add retry logic for failed requests
                HttpResponseMessage? response = null;
                string? content = null;
                int maxRetries = 3;
                int currentTry = 0;
                
                while (currentTry < maxRetries)
                {
                    try
                    {
                        response = await httpClient.GetAsync(feed.url);
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
                
                _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Downloaded {content.Length} bytes from {feed.url}");
                
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
                _logger.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Error processing feed {feed.name}: {ex.Message}");
                _logger.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Stack trace: {ex.StackTrace}");
            }
        }

        stopwatch.Stop();
        _logger.LogInformation($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Feed refresh completed in {stopwatch.ElapsedMilliseconds}ms");
    }
}
