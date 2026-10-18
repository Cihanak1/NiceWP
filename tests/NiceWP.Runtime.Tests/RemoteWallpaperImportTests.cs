using System.Net;
using System.Text;
using System.Text.Json;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Remote;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class RemoteWallpaperImportTests : IDisposable
{
    private static readonly byte[] ValidPngBytes =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89
    ];

    private static readonly byte[] ValidJpegBytes =
    [
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F',
        0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    ];

    private static readonly byte[] ValidMp4Bytes =
    [
        0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
        (byte)'m', (byte)'p', (byte)'4', (byte)'2', 0x00, 0x00, 0x00, 0x00,
        (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'m', (byte)'p', (byte)'4', (byte)'2'
    ];

    private readonly string testRootDirectory;
    private readonly string libraryFilePath;
    private readonly string tempDirectory;
    private readonly string storageDirectory;
    private readonly WallpaperLibraryService libraryService;

    public RemoteWallpaperImportTests()
    {
        testRootDirectory = Path.Combine(Path.GetTempPath(), $"NiceWP_RemoteTests_{Guid.NewGuid():N}");
        libraryFilePath = Path.Combine(testRootDirectory, "library.json");
        tempDirectory = Path.Combine(testRootDirectory, "temp", "remote");
        storageDirectory = Path.Combine(testRootDirectory, "library", "remote");

        Directory.CreateDirectory(testRootDirectory);
        Directory.CreateDirectory(tempDirectory);
        Directory.CreateDirectory(storageDirectory);

        libraryService = new WallpaperLibraryService(libraryFilePath);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testRootDirectory))
            {
                Directory.Delete(testRootDirectory, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup errors in tests
        }
    }

    private static Task<IPAddress[]> PublicDnsResolver(string host, CancellationToken ct) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static Task<IPAddress[]> LoopbackDnsResolver(string host, CancellationToken ct) =>
        Task.FromResult(new[] { IPAddress.Parse("127.0.0.1") });

    private static Task<IPAddress[]> PrivateDnsResolver(string host, CancellationToken ct) =>
        Task.FromResult(new[] { IPAddress.Parse("192.168.1.50") });

    // 1. Valid HTTPS image
    [Fact]
    public async Task ImportAsync_ValidHttpsImage_DownloadsValidatesAndAddsToLibrary()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidPngBytes)
                {
                    Headers = { { "Content-Type", "image/png" } }
                }
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/wallpapers/forest.png?token=secret123");

        Assert.True(result.Success);
        Assert.NotNull(result.Item);
        Assert.Equal(IpcConstants.WallpaperTypeImage, result.Item.Type);
        Assert.True(result.Item.IsRemoteImported);
        Assert.Equal("example.com", result.Item.SourceHost);
        Assert.Equal("https://example.com/wallpapers/forest.png", result.Item.SourceDisplayUrl);
        Assert.True(File.Exists(result.Item.FilePath));
        Assert.StartsWith(storageDirectory, result.Item.FilePath, StringComparison.OrdinalIgnoreCase);

        // Verify temp directory has no leftover files
        Assert.Empty(Directory.GetFiles(tempDirectory));
    }

    // 2. Valid HTTPS video
    [Fact]
    public async Task ImportAsync_ValidHttpsVideo_DownloadsValidatesAndAddsToLibrary()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidMp4Bytes)
                {
                    Headers = { { "Content-Type", "video/mp4" } }
                }
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/clips/waves.mp4");

        Assert.True(result.Success);
        Assert.NotNull(result.Item);
        Assert.Equal(IpcConstants.WallpaperTypeVideo, result.Item.Type);
        Assert.True(result.Item.IsRemoteImported);
        Assert.True(File.Exists(result.Item.FilePath));
    }

    // 3. Invalid scheme
    [Theory]
    [InlineData("ftp://example.com/pic.jpg")]
    [InlineData("file:///C:/Users/test/pic.jpg")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("javascript:alert(1)")]
    [InlineData("custom://test/wallpaper.jpg")]
    [InlineData("https://user:password@example.com/pic.jpg")]
    [InlineData("not a valid url")]
    [InlineData("")]
    public async Task ImportAsync_InvalidScheme_RejectsWithInvalidUrl(string invalidUrl)
    {
        var testHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync(invalidUrl);

        Assert.False(result.Success);
        Assert.Equal(NetworkSafetyValidator.ErrorInvalidUrl, result.ErrorMessage);
    }

    // 4. localhost/private IP reject
    [Theory]
    [InlineData("http://localhost/wallpaper.jpg")]
    [InlineData("http://127.0.0.1/wallpaper.jpg")]
    [InlineData("http://127.0.0.254:8080/wallpaper.jpg")]
    [InlineData("http://192.168.1.1/wallpaper.jpg")]
    [InlineData("http://10.0.0.1/wallpaper.jpg")]
    [InlineData("http://172.16.0.1/wallpaper.jpg")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/wallpaper.jpg")]
    [InlineData("http://test.local/wallpaper.jpg")]
    [InlineData("http://test.internal/wallpaper.jpg")]
    public async Task ImportAsync_LocalOrPrivateAddress_RejectsWithSsrfProtection(string privateUrl)
    {
        var testHandler = new TestHttpMessageHandler();
        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync(privateUrl);

        Assert.False(result.Success);
        Assert.Equal(NetworkSafetyValidator.ErrorLocalPrivateNotAllowed, result.ErrorMessage);
    }

    // 5. Redirect to private IP reject
    [Fact]
    public async Task ImportAsync_RedirectToPrivateAddress_RejectsRedirect()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req =>
            {
                if (req.RequestUri?.AbsolutePath == "/start")
                {
                    var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                    redirect.Headers.Location = new Uri("http://127.0.0.1/internal-secret.jpg");
                    return redirect;
                }
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/start");

        Assert.False(result.Success);
        Assert.Equal(NetworkSafetyValidator.ErrorLocalPrivateNotAllowed, result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(tempDirectory));
    }

    // 6. Oversized content-length rejected before download
    [Fact]
    public async Task ImportAsync_OversizedContentLength_RejectsBeforeDownload()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ValidPngBytes)
                };
                response.Content.Headers.ContentLength = 200 * 1024 * 1024; // 200 MB header for image
                response.Content.Headers.Add("Content-Type", "image/png");
                return response;
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/huge.png");

        Assert.False(result.Success);
        Assert.Equal("File is too large.", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(tempDirectory));
    }

    // 7. Stream exceeds limit without Content-Length
    [Fact]
    public async Task ImportAsync_StreamExceedsLimitWithoutContentLength_AbortsAndCleansTemp()
    {
        // Custom stream that generates more than MaxImageSizeBytes
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req =>
            {
                var endlessStream = new EndlessChunkStream(totalBytesToEmit: 110 * 1024 * 1024);
                var content = new StreamContent(endlessStream);
                content.Headers.Add("Content-Type", "image/png");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/stream.png");

        Assert.False(result.Success);
        Assert.Equal("File is too large.", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(tempDirectory));
    }

    // 8. Cancellation cleans temp
    [Fact]
    public async Task ImportAsync_Cancellation_CleansTempAndReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();

        var testHandler = new TestHttpMessageHandler
        {
            AsyncHandler = async (req, ct) =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(ValidPngBytes)
                };
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/cancel.png", cancellationToken: cts.Token);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Equal("Download cancelled.", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(tempDirectory));
    }

    // 9. Fake MIME / invalid media rejected
    [Fact]
    public async Task ImportAsync_FakeMimeOrInvalidMedia_RejectsAndCleansTemp()
    {
        var htmlContent = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Login page</body></html>");

        var testHandler = new TestHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(htmlContent)
                {
                    Headers = { { "Content-Type", "image/jpeg" } } // Spoofed MIME
                }
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/fake-wallpaper.jpg");

        Assert.False(result.Success);
        Assert.Equal("Downloaded file is not valid media.", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(tempDirectory));

        // Verify library was not updated
        var loaded = await libraryService.LoadAsync();
        Assert.Empty(loaded.Items);
    }

    // 10. Successful import creates local library item in remote storage
    [Fact]
    public async Task ImportAsync_SuccessfulImport_CreatesLocalFileInRemoteStorage()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidJpegBytes)
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://cdn.example.com/nature/mountain_view.jpeg?w=1920&q=80");

        Assert.True(result.Success);
        Assert.NotNull(result.Item);
        Assert.Equal("mountain_view.jpg", result.Item.DisplayName);
        Assert.True(File.Exists(result.Item.FilePath));
        Assert.StartsWith(storageDirectory, result.Item.FilePath, StringComparison.OrdinalIgnoreCase);

        // Path cannot contain path traversal
        Assert.DoesNotContain("..", result.Item.FilePath);

        var library = await libraryService.LoadAsync();
        Assert.Single(library.Items);
        Assert.Equal(result.Item.Id, library.Items[0].Id);
    }

    // 11. Apply failure keeps imported item
    [Fact]
    public async Task ImportFromUrlAsync_ApplyFailure_KeepsImportedItemInLibrary()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidPngBytes)
            }
        };

        var httpClient = new HttpClient(testHandler);
        var remoteService = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        // Simulate host failure on apply
        var controller = new WallpaperLibraryController(
            service: libraryService,
            ensureHostRunningAsync: ct => Task.FromResult(IpcResponse.Fail("test", "Runtime unavailable.")),
            remoteImportService: remoteService);

        var result = await controller.ImportFromUrlAsync("https://example.com/keep_on_failure.png", applyAfterDownload: true);

        Assert.True(result.Success);
        Assert.Equal("Wallpaper downloaded but could not be applied.", result.Message);
        Assert.NotNull(result.Entry);

        // Verify item is retained in library despite runtime host failure
        Assert.Single(controller.Items);
        Assert.Equal(result.Entry.Id, controller.Items[0].Id);
    }

    // 12. Old library.json compatibility
    [Fact]
    public async Task Library_OldJsonWithoutRemoteFields_LoadsCleanlyAndBackwardCompatible()
    {
        // Legacy JSON schema with no remote fields
        var legacyJson = """
        {
            "Version": 1,
            "Items": [
                {
                    "Id": "item-legacy-01",
                    "Type": "Image",
                    "FilePath": "C:\\Windows\\Web\\Wallpaper\\Theme1\\img1.jpg",
                    "DisplayName": "Theme 1",
                    "AddedAtUtc": "2026-01-01T00:00:00+00:00",
                    "IsFavorite": false
                }
            ]
        }
        """;

        await File.WriteAllTextAsync(libraryFilePath, legacyJson);

        var library = await libraryService.LoadAsync();

        Assert.Equal(1, library.Version);
        Assert.Single(library.Items);
        var item = library.Items[0];
        Assert.Equal("item-legacy-01", item.Id);
        Assert.False(item.IsRemoteImported);
        Assert.Null(item.SourceHost);
        Assert.Null(item.SourceDisplayUrl);
    }

    // 13. URL sanitization removes query/token/credentials
    [Fact]
    public void NetworkSafety_SanitizesUrl_RemovesQueryTokenAndCredentials()
    {
        var rawUri = new Uri("https://user:password@wallpaper.example.com:8443/landscapes/beach.jpg?auth=token123&track=analytics#slide2");

        var sanitized = NetworkSafetyValidator.SanitizeUrl(rawUri);

        Assert.Equal("https://wallpaper.example.com:8443/landscapes/beach.jpg", sanitized);
        Assert.DoesNotContain("token123", sanitized);
        Assert.DoesNotContain("user", sanitized);
        Assert.DoesNotContain("password", sanitized);
        Assert.DoesNotContain("slide2", sanitized);
    }

    // 14. Timeout cleanup
    [Fact]
    public async Task ImportAsync_Timeout_CleansTempAndReturnsTimeoutMessage()
    {
        var testHandler = new TestHttpMessageHandler
        {
            AsyncHandler = async (req, ct) =>
            {
                await Task.Delay(10, ct);
                throw new TimeoutException("Connection timed out.");
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var result = await service.ImportAsync("https://example.com/timeout.png");

        Assert.False(result.Success);
        Assert.Equal("Download timed out.", result.ErrorMessage);
        Assert.Empty(Directory.GetFiles(tempDirectory));
    }

    // 15. Duplicate source URL reuses existing local item
    [Fact]
    public async Task ImportAsync_DuplicateSourceUrl_ReusesExistingLocalItem()
    {
        var testHandler = new TestHttpMessageHandler
        {
            Handler = req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(ValidPngBytes)
            }
        };

        var httpClient = new HttpClient(testHandler);
        var service = new RemoteWallpaperImportService(
            libraryService,
            httpClient,
            PublicDnsResolver,
            tempDirectory,
            storageDirectory);

        var firstResult = await service.ImportAsync("https://example.com/sunset.png?signed=tokenA");
        Assert.True(firstResult.Success);
        Assert.False(firstResult.AlreadyExisted);

        // Second import of the same source with different token
        var secondResult = await service.ImportAsync("https://example.com/sunset.png?signed=tokenB");
        Assert.True(secondResult.Success);
        Assert.True(secondResult.AlreadyExisted);
        Assert.Equal(firstResult.Item?.Id, secondResult.Item?.Id);
        Assert.Equal(firstResult.Item?.FilePath, secondResult.Item?.FilePath);

        // Only one item in library
        var library = await libraryService.LoadAsync();
        Assert.Single(library.Items);
    }

    #region Helper Types

    private sealed class TestHttpMessageHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? AsyncHandler { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (AsyncHandler != null)
            {
                return await AsyncHandler(request, cancellationToken);
            }

            if (Handler != null)
            {
                return Handler(request);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class EndlessChunkStream : Stream
    {
        private readonly long totalBytes;
        private long bytesEmitted;

        public EndlessChunkStream(long totalBytesToEmit)
        {
            totalBytes = totalBytesToEmit;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position
        {
            get => bytesEmitted;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (bytesEmitted >= totalBytes)
            {
                return 0;
            }

            var toEmit = (int)Math.Min(count, totalBytes - bytesEmitted);
            Array.Fill(buffer, (byte)0x42, offset, toEmit);
            bytesEmitted += toEmit;
            return toEmit;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
