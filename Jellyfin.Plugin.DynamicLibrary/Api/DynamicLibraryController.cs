using System.Text.Json;
using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

/// <summary>
/// Controller for Dynamic Library API endpoints.
/// </summary>
[ApiController]
[Route("DynamicLibrary")]
public class DynamicLibraryController : ControllerBase
{
    private readonly DynamicItemCache _itemCache;
    private readonly SubtitleService _subtitleService;
    private readonly PersistenceService _persistenceService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITmdbClient _tmdbClient;
    private readonly ITvdbClient _tvdbClient;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly ILogger<DynamicLibraryController> _logger;

    public DynamicLibraryController(
        DynamicItemCache itemCache,
        SubtitleService subtitleService,
        PersistenceService persistenceService,
        IHttpClientFactory httpClientFactory,
        ITmdbClient tmdbClient,
        ITvdbClient tvdbClient,
        SearchResultFactory searchResultFactory,
        ILogger<DynamicLibraryController> logger)
    {
        _itemCache = itemCache;
        _subtitleService = subtitleService;
        _persistenceService = persistenceService;
        _httpClientFactory = httpClientFactory;
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
        _searchResultFactory = searchResultFactory;
        _logger = logger;
    }

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Get recently released movies for the GtV home screen.
    /// </summary>
    [HttpGet("Home/NewReleaseMovies")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> GetNewReleaseMovies(
        CancellationToken cancellationToken)
    {
        return GetTmdbNewReleasesAsync(false, cancellationToken);
    }

    /// <summary>
    /// Get recently released TV series for the GtV home screen.
    /// </summary>
    [HttpGet("Home/NewReleaseTV")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<IActionResult> GetNewReleaseTV(
        CancellationToken cancellationToken)
    {
        return GetTmdbNewReleasesAsync(true, cancellationToken);
    }

    private async Task<IActionResult> GetTmdbNewReleasesAsync(
        bool isTv,
        CancellationToken cancellationToken)
    {
        var apiKey = Config.TmdbApiKey;

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "[DynamicLibrary] TMDB API key is not configured");

            return Ok(
                new
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0,
                    StartIndex = 0
                });
        }

        var today = DateTime.UtcNow.Date;
        var earliest = today.AddDays(-60);

        var endpoint = isTv ? "discover/tv" : "discover/movie";
        var dateField = isTv ? "first_air_date" : "primary_release_date";

        var url =
            $"https://api.themoviedb.org/3/{endpoint}" +
            $"?api_key={Uri.EscapeDataString(apiKey)}" +
            "&include_adult=false" +
            "&language=en-US" +
            "&page=1" +
            "&sort_by=popularity.desc" +
            $"&{dateField}.gte={earliest:yyyy-MM-dd}" +
            $"&{dateField}.lte={today:yyyy-MM-dd}";

        var client = _httpClientFactory.CreateClient();

        var response =
            await client.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "[DynamicLibrary] TMDB new releases request failed: {StatusCode}",
                response.StatusCode);

            return Ok(
                new
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0,
                    StartIndex = 0
                });
        }

        var json =
            await response.Content.ReadAsStringAsync(cancellationToken);

        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("results", out var results))
        {
            return Ok(
                new
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0,
                    StartIndex = 0
                });
        }

        var items = new List<BaseItemDto>();

        foreach (var result in results.EnumerateArray().Take(20))
        {
            if (!result.TryGetProperty("id", out var idElement))
            {
                continue;
            }

            var id = idElement.GetInt32();

            var nameProperty = isTv ? "name" : "title";
            var originalNameProperty =
                isTv ? "original_name" : "original_title";

            var dateProperty =
                isTv ? "first_air_date" : "release_date";

            var name =
                result.TryGetProperty(nameProperty, out var nameElement)
                    ? nameElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var originalName =
                result.TryGetProperty(
                    originalNameProperty,
                    out var originalNameElement)
                    ? originalNameElement.GetString()
                    : null;

            var overview =
                result.TryGetProperty("overview", out var overviewElement)
                    ? overviewElement.GetString()
                    : null;

            var posterPath =
                result.TryGetProperty("poster_path", out var posterElement) &&
                posterElement.ValueKind != JsonValueKind.Null
                    ? posterElement.GetString()
                    : null;

            var backdropPath =
                result.TryGetProperty("backdrop_path", out var backdropElement) &&
                backdropElement.ValueKind != JsonValueKind.Null
                    ? backdropElement.GetString()
                    : null;

            var originalLanguage =
                result.TryGetProperty(
                    "original_language",
                    out var languageElement)
                    ? languageElement.GetString()
                    : null;

            DateTime? releaseDate = null;

            if (result.TryGetProperty(dateProperty, out var dateElement))
            {
                var rawDate = dateElement.GetString();

                if (DateTime.TryParse(rawDate, out var parsedDate))
                {
                    releaseDate = parsedDate;
                }
            }

            double? rating = null;

            if (result.TryGetProperty(
                    "vote_average",
                    out var ratingElement) &&
                ratingElement.TryGetDouble(out var parsedRating) &&
                parsedRating > 0)
            {
                rating = parsedRating;
            }
            Jellyfin.Plugin.DynamicLibrary.Providers.CatalogItem catalogItem;

            if (isTv)
            {
                var tvdbResults =
                    await _tvdbClient.SearchSeriesAsync(
                        name,
                        cancellationToken);

                var releaseYear = releaseDate?.Year;

                var tvdbMatch =
                    tvdbResults
                        .OrderByDescending(
                            tvdb =>
                                string.Equals(
                                    tvdb.Name,
                                    name,
                                    StringComparison.OrdinalIgnoreCase))
                        .ThenByDescending(
                            tvdb =>
                                releaseYear.HasValue &&
                                int.TryParse(tvdb.Year, out var tvdbYear) &&
                                tvdbYear == releaseYear.Value)
                        .FirstOrDefault();

                if (tvdbMatch == null)
                {
                    _logger.LogInformation(
                        "[DynamicLibrary] No TVDB match for new release series: {Name}",
                        name);

                    continue;
                }

                var language =
                    Config.GetTvdbLanguageCode();

                catalogItem =
                    new Jellyfin.Plugin.DynamicLibrary.Providers.CatalogItem
                    {
                        Id = tvdbMatch.TvdbIdInt.ToString(),
                        Source =
                            Jellyfin.Plugin.DynamicLibrary.Providers.CatalogSource.Tvdb,
                        TvdbId = tvdbMatch.TvdbIdInt.ToString(),
                        Name = tvdbMatch.GetLocalizedName(language),
                        OriginalName =
                            tvdbMatch.Name != tvdbMatch.GetLocalizedName(language)
                                ? tvdbMatch.Name
                                : null,
                        Overview =
                            tvdbMatch.GetLocalizedOverview(language),
                        PosterUrl = tvdbMatch.ImageUrl,
                        Year =
                            int.TryParse(tvdbMatch.Year, out var tvdbYear)
                                ? tvdbYear
                                : releaseYear,
                        ReleaseDate =
                            DateTime.TryParse(
                                tvdbMatch.FirstAirTime,
                                out var tvdbReleaseDate)
                                ? tvdbReleaseDate
                                : releaseDate,
                        Type =
                            Jellyfin.Plugin.DynamicLibrary.Providers.CatalogContentType.Series,
                        OriginalLanguage =
                            tvdbMatch.PrimaryLanguage
                    };

                _logger.LogInformation(
                    "[DynamicLibrary] Matched new release TV '{Name}' to TVDB {TvdbId}",
                    name,
                    tvdbMatch.TvdbIdInt);
            }
            else
            {
                catalogItem =
                    new Jellyfin.Plugin.DynamicLibrary.Providers.CatalogItem
                    {
                        Id = id.ToString(),
                        Source =
                            Jellyfin.Plugin.DynamicLibrary.Providers.CatalogSource.Tmdb,
                        TmdbId = id.ToString(),
                        Name = name,
                        OriginalName =
                            originalName != name
                                ? originalName
                                : null,
                        Overview = overview,
                        PosterUrl =
                            !string.IsNullOrWhiteSpace(posterPath)
                                ? $"https://image.tmdb.org/t/p/w500{posterPath}"
                                : null,
                        BackdropUrl =
                            !string.IsNullOrWhiteSpace(backdropPath)
                                ? $"https://image.tmdb.org/t/p/original{backdropPath}"
                                : null,
                        Year = releaseDate?.Year,
                        ReleaseDate = releaseDate,
                        Rating = rating,
                        Type =
                            Jellyfin.Plugin.DynamicLibrary.Providers.CatalogContentType.Movie,
                        OriginalLanguage = originalLanguage
                    };
            }

            var dto =
                await _searchResultFactory.CreateDtoFromCatalogItemAsync(
                    catalogItem,
                    cancellationToken);

            items.Add(dto);
        }

        _logger.LogInformation(
            "[DynamicLibrary] Returning {Count} new release {Type} items",
            items.Count,
            isTv ? "TV" : "movie");

        return Ok(
            new
            {
                Items = items,
                TotalRecordCount = items.Count,
                StartIndex = 0
            });
    }

    /// <summary>
    /// Get a dynamic item by ID.
    /// </summary>
    [HttpGet("Items/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BaseItemDto> GetItem([FromRoute] Guid itemId)
    {
        var item = _itemCache.GetItem(itemId);
        if (item == null)
        {
            _logger.LogDebug("[DynamicLibrary] Item not found in cache: {ItemId}", itemId);
            return NotFound();
        }

        _logger.LogDebug("[DynamicLibrary] Returning cached item: {Name} ({Id})", item.Name, itemId);
        return Ok(item);
    }

    /// <summary>
    /// Get image for a dynamic item.
    /// </summary>
    [HttpGet("Items/{itemId}/Images/Primary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItemImage(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var imageUrl = _itemCache.GetImageUrl(itemId);
        if (string.IsNullOrEmpty(imageUrl))
        {
            _logger.LogDebug("[DynamicLibrary] No image URL for item: {ItemId}", itemId);
            return NotFound();
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(imageUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogInformation("[DynamicLibrary] Failed to fetch image from {Url}: {Status}",
                    imageUrl, response.StatusCode);
                return NotFound();
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var imageData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            return File(imageData, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching image from {Url}", imageUrl);
            return NotFound();
        }
    }

    /// <summary>
    /// Get subtitle for a dynamic item in WebVTT format.
    /// </summary>
    [HttpGet("Subtitles/{itemId}/{languageCode}.vtt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSubtitleVtt(
        [FromRoute] Guid itemId,
        [FromRoute] string languageCode,
        CancellationToken cancellationToken)
    {
        var content = await _subtitleService.GetSubtitleContentAsync(itemId, languageCode, cancellationToken);
        if (content == null)
        {
            _logger.LogInformation("[DynamicLibrary] Subtitle not found: {ItemId}, {Language}", itemId, languageCode);
            return NotFound();
        }

        _logger.LogDebug("[DynamicLibrary] Serving VTT subtitle: {ItemId}, {Language}, ContentLength={Length}",
            itemId, languageCode, content.Length);

        return Content(content, "text/vtt", Encoding.UTF8);
    }

    /// <summary>
    /// Get subtitle for a dynamic item in JSON TrackEvents format.
    /// Used by Jellyfin web player for custom subtitle rendering.
    /// </summary>
    [HttpGet("Subtitles/{itemId}/{languageCode}.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSubtitleJs(
        [FromRoute] Guid itemId,
        [FromRoute] string languageCode,
        CancellationToken cancellationToken)
    {
        var content = await _subtitleService.GetSubtitleContentAsync(itemId, languageCode, cancellationToken);
        if (content == null)
        {
            _logger.LogInformation("[DynamicLibrary] Subtitle not found: {ItemId}, {Language}", itemId, languageCode);
            return NotFound();
        }

        // Convert WebVTT to TrackEvents JSON
        var trackEventsJson = SubtitleConverter.WebVttToTrackEvents(content);

        _logger.LogDebug("[DynamicLibrary] Serving JS subtitle: {ItemId}, {Language}, ContentLength={Length}",
            itemId, languageCode, trackEventsJson.Length);

        return Content(trackEventsJson, "application/json", Encoding.UTF8);
    }

    /// <summary>
    /// Persist a dynamic item to the library as a .strm file.
    /// </summary>
    /// <param name="itemId">The ID of the dynamic item to persist.</param>
    /// <returns>The path to the created item, or an error if persistence failed.</returns>
    [HttpPost("Persist/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PersistItem(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        // Check if persistence is enabled
        if (!Config.EnablePersistence)
        {
            _logger.LogWarning("[DynamicLibrary] Persistence is not enabled");
            return BadRequest(new { Error = "Persistence is not enabled in plugin settings" });
        }

        // Get the item from cache
        var item = _itemCache.GetItem(itemId);
        if (item == null)
        {
            _logger.LogDebug("[DynamicLibrary] Item not found in cache for persistence: {ItemId}", itemId);
            return NotFound(new { Error = "Item not found in cache" });
        }

        _logger.LogInformation("[DynamicLibrary] Persisting item: {Name} ({Type})", item.Name, item.Type);

        string? createdPath = null;

        try
        {
            // Handle based on item type
            if (item.Type == BaseItemKind.Movie)
            {
                createdPath = await _persistenceService.PersistMovieAsync(item, cancellationToken);
            }
            else if (item.Type == BaseItemKind.Series)
            {
                createdPath = await _persistenceService.PersistSeriesAsync(item, cancellationToken);
            }
            else
            {
                _logger.LogWarning("[DynamicLibrary] Cannot persist item type: {Type}", item.Type);
                return BadRequest(new { Error = $"Cannot persist item type: {item.Type}" });
            }

            if (createdPath == null)
            {
                _logger.LogDebug("[DynamicLibrary] Item already exists in library or could not be created: {Name}", item.Name);
                return Ok(new { Message = "Item already exists in library", AlreadyExists = true });
            }

            // Trigger library scan if configured
            _persistenceService.TriggerLibraryScan();

            _logger.LogInformation("[DynamicLibrary] Successfully persisted: {Name} to {Path}", item.Name, createdPath);
            return Ok(new { Message = "Item persisted successfully", Path = createdPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error persisting item: {Name}", item.Name);
            return BadRequest(new { Error = $"Failed to persist item: {ex.Message}" });
        }
    }
}




