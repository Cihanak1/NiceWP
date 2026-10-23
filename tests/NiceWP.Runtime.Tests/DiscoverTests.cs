using System.Net;
using System.Text;
using NiceWP.Core.Discover;
using NiceWP.Infrastructure.Discover;
using NiceWP.Infrastructure.Library;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class DiscoverTests : IDisposable
{
    private readonly string _tempDirectory;

    public DiscoverTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_DiscoverTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    #region Helper Test Http Handler and Models

    private sealed class DelegatingTestHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public DelegatingTestHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_handler(request));
        }
    }

    private static OnlineWallpaperItem CreateTestItem(
        string remoteId = "test-1",
        string providerId = "openverse",
        string title = "Test Wallpaper",
        string creatorName = "Test Creator",
        string downloadUrl = "https://example.com/image.jpg",
        string licenseCode = "CC0",
        bool isPublicDomain = true)
    {
        return new OnlineWallpaperItem(
            ProviderId: providerId,
            RemoteId: remoteId,
            Title: title,
            Description: "Test Description",
            MediaType: "image",
            Width: 1920,
            Height: 1080,
            AspectRatio: 1.77,
            ThumbnailUrl: "https://example.com/thumb.jpg",
            PreviewUrl: "https://example.com/preview.jpg",
            DownloadUrl: downloadUrl,
            CreatorName: creatorName,
            CreatorUrl: "https://example.com/creator",
            SourceName: providerId,
            SourceUrl: "https://example.com/source",
            LicenseCode: licenseCode,
            LicenseName: licenseCode,
            LicenseUrl: "https://example.com/license",
            AttributionText: $"\"{title}\" by {creatorName}",
            IsPublicDomain: isPublicDomain,
            RequiresAttribution: !isPublicDomain,
            CommercialUseAllowed: true,
            DerivativeUseAllowed: true,
            Tags: ["test", "nature"]);
    }

    #endregion

    #region 1. Provider & License Policy Tests

    [Fact]
    public void OnlineProviderPolicy_AllowedProviders_MatchStrictWhitelist()
    {
        Assert.True(OnlineProviderPolicy.IsAllowedProductionProvider("openverse"));
        Assert.True(OnlineProviderPolicy.IsAllowedProductionProvider("wikimedia"));
        Assert.True(OnlineProviderPolicy.IsAllowedProductionProvider("nasa"));

        // Case-insensitivity check
        Assert.True(OnlineProviderPolicy.IsAllowedProductionProvider("OpenVerse"));
        Assert.True(OnlineProviderPolicy.IsAllowedProductionProvider("WIKIMEDIA"));
        Assert.True(OnlineProviderPolicy.IsAllowedProductionProvider("Nasa"));
    }

    [Theory]
    [InlineData("unsplash")]
    [InlineData("pexels")]
    [InlineData("pixabay")]
    [InlineData("wallhaven")]
    [InlineData("reddit")]
    [InlineData("google")]
    [InlineData("")]
    [InlineData(null)]
    public void OnlineProviderPolicy_ForbiddenProviders_AreRejected(string? provider)
    {
        Assert.False(OnlineProviderPolicy.IsAllowedProductionProvider(provider));
    }

    [Theory]
    [InlineData("pexels")]
    [InlineData("pixabay")]
    [InlineData("unsplash")]
    [InlineData("wallhaven")]
    public void OnlineProviderPolicy_ExplicitlyExcluded_ProvidesLegalRationale(string provider)
    {
        var isExcluded = OnlineProviderPolicy.IsExcludedProvider(provider, out var reason);
        Assert.True(isExcluded);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Theory]
    [InlineData("pdm", LicenseStatus.Allowed)]
    [InlineData("public domain", LicenseStatus.Allowed)]
    [InlineData("cc0", LicenseStatus.Allowed)]
    [InlineData("CC0 1.0", LicenseStatus.Allowed)]
    [InlineData("by", LicenseStatus.AllowedWithAttribution)]
    [InlineData("CC BY 4.0", LicenseStatus.AllowedWithAttribution)]
    [InlineData("by-sa", LicenseStatus.AllowedWithAttribution)]
    [InlineData("CC BY-SA 3.0", LicenseStatus.AllowedWithAttribution)]
    public void WallpaperLicensePolicy_PermittedOpenLicenses_EvaluateToAllowed(string code, LicenseStatus expectedStatus)
    {
        var info = WallpaperLicensePolicy.Evaluate(code);
        Assert.Equal(expectedStatus, info.Status);
        Assert.True(WallpaperLicensePolicy.IsDefaultDiscoverAllowed(info));
    }

    [Theory]
    [InlineData("by-nc")]
    [InlineData("CC BY-NC 4.0")]
    [InlineData("by-nd")]
    [InlineData("by-nc-sa")]
    [InlineData("by-nc-nd")]
    public void WallpaperLicensePolicy_RestrictedLicenses_EvaluateToRestricted(string code)
    {
        var info = WallpaperLicensePolicy.Evaluate(code);
        Assert.Equal(LicenseStatus.Restricted, info.Status);
        Assert.False(WallpaperLicensePolicy.IsDefaultDiscoverAllowed(info));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("xyz-unknown-custom")]
    [InlineData("all-rights-reserved")]
    [InlineData("copyrighted")]
    public void WallpaperLicensePolicy_UnknownOrEmpty_EvaluatesToUnknown(string? code)
    {
        var info = WallpaperLicensePolicy.Evaluate(code);
        Assert.Equal(LicenseStatus.Unknown, info.Status);
        Assert.False(WallpaperLicensePolicy.IsDefaultDiscoverAllowed(info));
    }

    #endregion

    #region 2. Openverse Provider Tests

    [Fact]
    public async Task OpenverseProvider_Search_ParsesResultsAndLicensesCorrectly()
    {
        const string mockJson = """
        {
            "result_count": 1,
            "page_count": 1,
            "page_size": 20,
            "page": 1,
            "results": [
                {
                    "id": "openverse-item-1",
                    "title": "Majestic Mountain Peaks",
                    "creator": "Jane Doe",
                    "creator_url": "https://example.com/jane",
                    "url": "https://images.example.com/mountain.jpg",
                    "thumbnail": "https://images.example.com/mountain_thumb.jpg",
                    "license": "by",
                    "license_version": "4.0",
                    "license_url": "https://creativecommons.org/licenses/by/4.0/",
                    "foreign_landing_url": "https://openverse.org/image/openverse-item-1",
                    "width": 3840,
                    "height": 2160,
                    "filesize": 4500000,
                    "filetype": "jpg",
                    "tags": [{ "name": "nature" }, { "name": "mountain" }]
                }
            ]
        }
        """;

        var handler = new DelegatingTestHttpHandler(req =>
        {
            Assert.Contains("mature=false", req.RequestUri!.Query);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var provider = new OpenverseWallpaperProvider(httpClient);

        var query = new WallpaperSearchQuery("mountain", Page: 1, PageSize: 20);
        var result = await provider.SearchAsync(query, CancellationToken.None);

        Assert.Equal("openverse", result.ProviderId);
        Assert.Single(result.Items);
        Assert.Null(result.ErrorMessage);

        var item = result.Items[0];
        Assert.Equal("openverse-item-1", item.RemoteId);
        Assert.Equal("Majestic Mountain Peaks", item.Title);
        Assert.Equal("Jane Doe", item.CreatorName);
        Assert.Equal("CC BY", item.LicenseCode);
        Assert.Equal(3840, item.Width);
        Assert.Equal(2160, item.Height);
        Assert.Equal("image", item.MediaType);
        Assert.NotNull(item.Tags);
        Assert.Contains("mountain", item.Tags);
    }

    [Fact]
    public async Task OpenverseProvider_Http429_TracksRateLimitStateGracefully()
    {
        var rateLimiter = new OpenverseRateLimitHandler();
        var handler = new DelegatingTestHttpHandler(req =>
        {
            var resp = new HttpResponseMessage((HttpStatusCode)429);
            resp.Headers.TryAddWithoutValidation("Retry-After", "30");
            return resp;
        });

        using var httpClient = new HttpClient(handler);
        var provider = new OpenverseWallpaperProvider(httpClient, rateLimiter);

        var query = new WallpaperSearchQuery("test", Page: 1, PageSize: 20);
        var result = await provider.SearchAsync(query, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("rate limited", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(provider.CurrentRateLimit?.IsLimited == true);
    }

    [Fact]
    public async Task OpenverseProvider_MalformedJson_ReturnsGracefulErrorMessage()
    {
        var handler = new DelegatingTestHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<!DOCTYPE html><html>Broken response</html>", Encoding.UTF8, "text/html")
        });

        using var httpClient = new HttpClient(handler);
        var provider = new OpenverseWallpaperProvider(httpClient);

        var query = new WallpaperSearchQuery("broken");
        var result = await provider.SearchAsync(query, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.NotNull(result.ErrorMessage);
    }

    #endregion

    #region 3. Wikimedia Commons Provider Tests

    [Fact]
    public async Task WikimediaProvider_Search_SanitizesHtmlAndNormalizesLicense()
    {
        const string mockJson = """
        {
            "query": {
                "pages": [
                    {
                        "pageid": 12345,
                        "title": "File:Lake Sunset Panorama.jpg",
                        "imageinfo": [
                            {
                                "url": "https://upload.wikimedia.org/wikipedia/commons/test.jpg",
                                "thumburl": "https://upload.wikimedia.org/wikipedia/commons/thumb/test.jpg/800px-test.jpg",
                                "descriptionurl": "https://commons.wikimedia.org/wiki/File:Lake_Sunset_Panorama.jpg",
                                "width": 4000,
                                "height": 2250,
                                "size": 3200000,
                                "mime": "image/jpeg",
                                "extmetadata": {
                                    "ObjectName": { "value": "Lake Sunset Panorama" },
                                    "Artist": { "value": "<a href=\"https://example.com\">John Photographer</a>" },
                                    "LicenseShortName": { "value": "CC BY-SA 4.0" },
                                    "LicenseUrl": { "value": "https://creativecommons.org/licenses/by-sa/4.0/" }
                                }
                            }
                        ]
                    }
                ]
            }
        }
        """;

        var handler = new DelegatingTestHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var provider = new WikimediaWallpaperProvider(httpClient);

        var query = new WallpaperSearchQuery("lake sunset");
        var result = await provider.SearchAsync(query, CancellationToken.None);

        Assert.Equal("wikimedia", result.ProviderId);
        Assert.Single(result.Items);

        var item = result.Items[0];
        Assert.Equal("12345", item.RemoteId);
        Assert.Equal("Lake Sunset Panorama", item.Title);
        // Artist HTML tags stripped
        Assert.Equal("John Photographer", item.CreatorName);
        Assert.Equal("CC BY-SA", item.LicenseCode);
        Assert.Equal(4000, item.Width);
    }

    [Fact]
    public void HtmlTextSanitizer_StripsTagsAndDecodesEntities()
    {
        var input = "Photo &copy; by <a href='https://example.com' class='author'>Jane &amp; Joe</a> &bull; <i>2026</i>";
        var cleaned = HtmlTextSanitizer.Sanitize(input);

        Assert.Equal("Photo © by Jane & Joe • 2026", cleaned);
    }

    #endregion

    #region 4. NASA Provider Tests

    [Fact]
    public async Task NasaProvider_Search_ExtractsImageAndAppliesRightsFilter()
    {
        const string mockJson = """
        {
            "collection": {
                "metadata": { "total_hits": 1 },
                "items": [
                    {
                        "href": "https://images-api.nasa.gov/asset/PIA12345",
                        "data": [
                            {
                                "nasa_id": "PIA12345",
                                "title": "Carina Nebula Pillars",
                                "center": "JPL",
                                "date_created": "2026-01-15T00:00:00Z",
                                "description": "High resolution view of Carina Nebula pillars captured by telescope.",
                                "media_type": "image",
                                "keywords": ["Nebula", "Space", "Stars"]
                            }
                        ],
                        "links": [
                            {
                                "href": "https://images-assets.nasa.gov/image/PIA12345/PIA12345~thumb.jpg",
                                "rel": "preview",
                                "render": "image"
                            }
                        ]
                    }
                ]
            }
        }
        """;

        var handler = new DelegatingTestHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(mockJson, Encoding.UTF8, "application/json")
        });

        using var httpClient = new HttpClient(handler);
        var provider = new NasaWallpaperProvider(httpClient);

        var query = new WallpaperSearchQuery("nebula");
        var result = await provider.SearchAsync(query, CancellationToken.None);

        Assert.Equal("nasa", result.ProviderId);
        Assert.Single(result.Items);

        var item = result.Items[0];
        Assert.Equal("PIA12345", item.RemoteId);
        Assert.Equal("Carina Nebula Pillars", item.Title);
        Assert.True(item.IsPublicDomain);
        Assert.Equal("PDM", item.LicenseCode);
        Assert.Equal("NASA / JPL", item.CreatorName);
        Assert.NotNull(item.Tags);
        Assert.Contains("Nebula", item.Tags);
    }

    [Fact]
    public void NasaRightsFilter_ExcludesThirdPartyCopyrightedMaterial()
    {
        var approved = NasaRightsFilter.IsSafeForDisplay(
            copyright: null,
            rights: "Public Domain",
            description: "NASA/JPL-Caltech image of the Martian surface.");

        Assert.True(approved);

        var rejected = NasaRightsFilter.IsSafeForDisplay(
            copyright: "Commercial Space Corp",
            rights: null,
            description: "Image Copyright © 2026 Commercial Space Corp. All rights reserved.");

        Assert.False(rejected);
    }

    #endregion

    #region 5. Discover Aggregator & Cache Tests

    [Fact]
    public async Task WallpaperDiscoverService_AggregatesAndInterleavesResults()
    {
        var dummyItem1 = CreateTestItem(remoteId: "1", providerId: "openverse", title: "Openverse Wallpaper", downloadUrl: "https://example.com/item1.jpg");
        var dummyItem2 = CreateTestItem(remoteId: "2", providerId: "nasa", title: "NASA Wallpaper", downloadUrl: "https://example.com/item2.jpg");

        var p1 = new MockProvider("openverse", [dummyItem1]);
        var p2 = new MockProvider("nasa", [dummyItem2]);

        var isolatedCache = new WallpaperDiscoverCache(Path.Combine(_tempDirectory, "test_cache_interleave"));
        var service = new WallpaperDiscoverService([p1, p2], isolatedCache);
        var query = new WallpaperSearchQuery($"nature-{Guid.NewGuid():N}");

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        // Interleaved: item from p1, then p2
        Assert.Equal("openverse", result.Items[0].ProviderId);
        Assert.Equal("nasa", result.Items[1].ProviderId);
    }

    [Fact]
    public async Task WallpaperDiscoverService_OfflineNetwork_ReturnsCleanOfflineMessage()
    {
        var p1 = new MockFailingProvider("openverse", new HttpRequestException("No such host is known."));
        var p2 = new MockFailingProvider("wikimedia", new HttpRequestException("NameResolutionFailure: timed out."));

        var isolatedCache = new WallpaperDiscoverCache(Path.Combine(_tempDirectory, "test_cache_offline"));
        var service = new WallpaperDiscoverService([p1, p2], isolatedCache);
        var query = new WallpaperSearchQuery($"offline-test-{Guid.NewGuid():N}");

        var result = await service.SearchAsync(query, CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal("No internet connection.", result.ErrorMessage);
    }

    [Fact]
    public async Task WallpaperDiscoverCache_CachesAndExpiresQueries()
    {
        var cacheDir = Path.Combine(_tempDirectory, "cache");
        var shortDuration = TimeSpan.FromMilliseconds(200);
        var cache = new WallpaperDiscoverCache(cacheDir, shortDuration);

        var query = new WallpaperSearchQuery("cached-test");
        var sampleResult = new WallpaperSearchResult(
            ProviderId: "all",
            Items: [CreateTestItem(remoteId: "c1", providerId: "openverse", title: "Cached Item")]);

        await cache.SetAsync(query, sampleResult, CancellationToken.None);

        // Immediate read -> hit
        var hit = await cache.TryGetAsync(query, CancellationToken.None);
        Assert.NotNull(hit);
        Assert.Single(hit.Items);
        Assert.Equal("Cached Item", hit.Items[0].Title);

        // Wait for expiration
        await Task.Delay(250);

        var expired = await cache.TryGetAsync(query, CancellationToken.None);
        Assert.Null(expired);
    }

    #endregion

    #region 6. Secure Online Downloader Tests

    [Fact]
    public async Task OnlineDownloader_ValidJpeg_DownloadsAndImportsToLibrary()
    {
        var libraryDir = Path.Combine(_tempDirectory, "library");
        var onlineDir = Path.Combine(_tempDirectory, "online");
        Directory.CreateDirectory(libraryDir);
        Directory.CreateDirectory(onlineDir);

        var libraryFilePath = Path.Combine(libraryDir, "library.json");
        var libraryService = new WallpaperLibraryService(libraryFilePath);

        // Minimal valid JPEG binary (SOI 0xFFD8, APP0 marker, SOF0, EOI 0xFFD9)
        var validJpegBytes = new byte[]
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
            0x01, 0x01, 0x00, 0x48, 0x00, 0x48, 0x00, 0x00, 0xFF, 0xD9
        };

        var handler = new DelegatingTestHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(validJpegBytes)
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new OnlineWallpaperDownloader(
            libraryService,
            analysisService: null,
            thumbnailService: null,
            httpClient: httpClient,
            baseOnlineDirectory: onlineDir);

        var onlineItem = CreateTestItem(
            remoteId: "nasa-photo-100",
            providerId: "nasa",
            title: "Galaxy Photo",
            creatorName: "NASA/Hubble",
            downloadUrl: "https://images-assets.nasa.gov/image/test/full.jpg",
            licenseCode: "PDM",
            isPublicDomain: true);

        var result = await downloader.DownloadAndImportAsync(onlineItem, null, CancellationToken.None);

        Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
        Assert.NotNull(result.LocalFilePath);
        Assert.True(File.Exists(result.LocalFilePath));
        var libItem = result.LibraryItem as WallpaperLibraryItem;
        Assert.NotNull(libItem);
        Assert.NotNull(libItem.Origin);
        Assert.Equal("nasa", libItem.Origin.Provider);
        Assert.Equal("nasa-photo-100", libItem.Origin.RemoteId);

        // Verify duplicate download detection
        var secondDownload = await downloader.DownloadAndImportAsync(onlineItem, null, CancellationToken.None);
        Assert.True(secondDownload.Success);
        Assert.True(secondDownload.AlreadyExisted);
        Assert.Equal(result.LocalFilePath, secondDownload.LocalFilePath);
    }

    [Theory]
    [InlineData("http://images.example.com/photo.jpg")] // Non-HTTPS rejected
    [InlineData("https://localhost/photo.jpg")]         // Localhost rejected
    [InlineData("https://127.0.0.1/photo.jpg")]         // Loopback rejected
    [InlineData("https://192.168.1.100/photo.jpg")]     // Private RFC1918 rejected
    [InlineData("https://10.0.0.5/photo.jpg")]          // Private RFC1918 rejected
    [InlineData("https://172.16.5.1/photo.jpg")]        // Private RFC1918 rejected
    [InlineData("https://169.254.169.254/latest/meta")] // Cloud metadata IP rejected
    public async Task OnlineDownloader_SsrfAndInsecureUrls_AreRejected(string badUrl)
    {
        var libraryDir = Path.Combine(_tempDirectory, "library_ssrf");
        var onlineDir = Path.Combine(_tempDirectory, "online_ssrf");
        var libraryService = new WallpaperLibraryService(Path.Combine(libraryDir, "library.json"));

        var downloader = new OnlineWallpaperDownloader(
            libraryService,
            baseOnlineDirectory: onlineDir);

        var badItem = CreateTestItem(
            remoteId: "bad-url-item",
            providerId: "openverse",
            downloadUrl: badUrl);

        var result = await downloader.DownloadAndImportAsync(badItem, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task OnlineDownloader_MagicBytesMismatch_FailsVerificationAndCleansUp()
    {
        var libraryDir = Path.Combine(_tempDirectory, "library_magic");
        var onlineDir = Path.Combine(_tempDirectory, "online_magic");
        var libraryService = new WallpaperLibraryService(Path.Combine(libraryDir, "library.json"));

        // Plain text file pretending to be image (spoofed extension or payload)
        var fakeBytes = Encoding.UTF8.GetBytes("This is not an image binary!");

        var handler = new DelegatingTestHttpHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(fakeBytes)
        });

        using var httpClient = new HttpClient(handler);
        var downloader = new OnlineWallpaperDownloader(
            libraryService,
            httpClient: httpClient,
            baseOnlineDirectory: onlineDir);

        var item = CreateTestItem(
            remoteId: "spoofed-item",
            providerId: "wikimedia",
            downloadUrl: "https://upload.wikimedia.org/wikipedia/commons/test.jpg",
            licenseCode: "CC BY-SA",
            isPublicDomain: false);

        var result = await downloader.DownloadAndImportAsync(item, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("media signature", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // Verify temp directory cleaned up
        var tempDir = Path.Combine(onlineDir, "temp");
        if (Directory.Exists(tempDir))
        {
            var tempFiles = Directory.GetFiles(tempDir);
            Assert.Empty(tempFiles);
        }
    }

    #endregion

    #region 7. Library Provenance & Persistence Tests

    [Fact]
    public async Task WallpaperLibrary_PersistsAndLoadsOriginProvenance()
    {
        var libraryDir = Path.Combine(_tempDirectory, "provenance_lib");
        Directory.CreateDirectory(libraryDir);
        var libraryFilePath = Path.Combine(libraryDir, "library.json");
        var libraryService = new WallpaperLibraryService(libraryFilePath);

        var origin = new WallpaperOrigin
        {
            Provider = "openverse",
            RemoteId = "ov-999",
            OriginalUrl = "https://openverse.org/image/ov-999",
            Creator = "Alice Visuals",
            License = "CC BY",
            LicenseUrl = "https://creativecommons.org/licenses/by/4.0/",
            AttributionText = "\"Forest Mist\" by Alice Visuals is licensed under CC BY 4.0"
        };

        var dummyFilePath = Path.Combine(libraryDir, "sample.jpg");
        await File.WriteAllBytesAsync(dummyFilePath, [0xFF, 0xD8, 0xFF, 0xD9]);

        var libraryItem = new WallpaperLibraryItem
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = "Forest Mist",
            FilePath = dummyFilePath,
            Type = "image",
            AddedAtUtc = DateTimeOffset.UtcNow,
            Origin = origin
        };

        var library = new WallpaperLibrary
        {
            Items = [libraryItem]
        };

        await libraryService.SaveAsync(library, CancellationToken.None);

        // Reload from disk
        var loaded = await libraryService.LoadAsync(CancellationToken.None);
        Assert.Single(loaded.Items);

        var reloadedItem = loaded.Items[0];
        Assert.NotNull(reloadedItem.Origin);
        Assert.Equal("openverse", reloadedItem.Origin.Provider);
        Assert.Equal("ov-999", reloadedItem.Origin.RemoteId);
        Assert.Equal("Alice Visuals", reloadedItem.Origin.Creator);
        Assert.Equal("CC BY", reloadedItem.Origin.License);

        // Verify FindByOrigin helper
        var found = loaded.FindByOrigin("openverse", "ov-999");
        Assert.NotNull(found);
        Assert.Equal(reloadedItem.Id, found.Id);
    }

    #endregion

    #region Mock Classes

    private sealed class MockProvider : IWallpaperProvider
    {
        private readonly IReadOnlyList<OnlineWallpaperItem> _items;

        public MockProvider(string providerId, IReadOnlyList<OnlineWallpaperItem> items)
        {
            ProviderId = providerId;
            _items = items;
        }

        public string ProviderId { get; }
        public string Id => ProviderId;
        public string DisplayName => ProviderId;
        public ProviderCapabilities Capabilities => new(SupportsVideo: false, SupportsOrientationFilter: false, SupportsResolutionFilter: false, SupportsLicenseFilter: false);
        public ProviderRateLimitState? CurrentRateLimit => new(IsLimited: false);

        public Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(new WallpaperSearchResult(ProviderId, _items));
        }

        public Task<OnlineWallpaperDetails?> GetDetailsAsync(string remoteId, CancellationToken cancellationToken)
        {
            return Task.FromResult<OnlineWallpaperDetails?>(null);
        }

        public Task<Stream?> OpenDownloadAsync(string downloadUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    private sealed class MockFailingProvider : IWallpaperProvider
    {
        private readonly Exception _exception;

        public MockFailingProvider(string providerId, Exception exception)
        {
            ProviderId = providerId;
            _exception = exception;
        }

        public string ProviderId { get; }
        public string Id => ProviderId;
        public string DisplayName => ProviderId;
        public ProviderCapabilities Capabilities => new(SupportsVideo: false, SupportsOrientationFilter: false, SupportsResolutionFilter: false, SupportsLicenseFilter: false);
        public ProviderRateLimitState? CurrentRateLimit => new(IsLimited: false);

        public Task<WallpaperSearchResult> SearchAsync(WallpaperSearchQuery query, CancellationToken cancellationToken)
        {
            return Task.FromResult(new WallpaperSearchResult(ProviderId, [], ErrorMessage: $"{_exception.GetType().Name}: {_exception.Message}"));
        }

        public Task<OnlineWallpaperDetails?> GetDetailsAsync(string remoteId, CancellationToken cancellationToken)
        {
            return Task.FromResult<OnlineWallpaperDetails?>(null);
        }

        public Task<Stream?> OpenDownloadAsync(string downloadUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult<Stream?>(null);
        }
    }

    #endregion
}
