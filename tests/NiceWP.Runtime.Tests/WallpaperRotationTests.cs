using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Rotation;
using NiceWP.Infrastructure.Settings;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class WallpaperRotationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _tempFiles = new();

    public WallpaperRotationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"NiceWP_RotationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore temp cleanup errors
        }
    }

    private WallpaperLibraryItem CreateItem(string id, string name, string type = "Image", bool isFavorite = false, bool existsOnDisk = true)
    {
        var filePath = Path.Combine(_tempDir, $"{id}.png");
        if (existsOnDisk && !File.Exists(filePath))
        {
            File.WriteAllBytes(filePath, [0x89, 0x50, 0x4E, 0x47]);
            _tempFiles.Add(filePath);
        }

        return new WallpaperLibraryItem
        {
            Id = id,
            DisplayName = name,
            FilePath = filePath,
            Type = type,
            IsFavorite = isFavorite
        };
    }

    private static WallpaperLibraryApplyResult SuccessResult(string path) =>
        new(Success: true, ActiveWallpaperPath: path);

    private static WallpaperLibraryApplyResult FailureResult(string message) =>
        new(Success: false, Message: message);

    private static IpcResponse OkResponse(string message = "ok") =>
        IpcResponse.Ok(Guid.NewGuid().ToString("N"), message);

    private sealed class TestRandomProvider : IRotationRandomProvider
    {
        private readonly Queue<int> _numbers;

        public TestRandomProvider(params int[] numbers)
        {
            _numbers = new Queue<int>(numbers);
        }

        public int Next(int maxValue)
        {
            if (_numbers.Count > 0)
            {
                var val = _numbers.Dequeue();
                return val % maxValue;
            }
            return 0;
        }
    }

    // 1. Rotation Off default: timer is not scheduled, IsActive is false
    [Fact]
    public async Task Scenario01_RotationOffDefault_DoesNotScheduleTimer()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        var settings = AppSettings.CreateDefault();
        await service.InitializeAsync(settings);

        var status = service.GetStatus();
        Assert.Equal(RotationMode.Off, status.Mode);
        Assert.False(status.IsActive);
        Assert.Null(status.NextDueTimeUtc);
        Assert.False(status.IsOverdue);
    }

    // 2. All Library sequential: iterates sequentially and wraps around
    [Fact]
    public async Task Scenario02_AllLibrarySequential_IteratesAndWrapsAround()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2"),
            CreateItem("w3", "Wallpaper 3")
        };

        string? appliedId = null;
        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                appliedId = item.Id;
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("All", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // Next -> w2
        var res1 = await service.RotateNextAsync();
        Assert.True(res1.Success);
        Assert.Equal("w2", appliedId);

        // Next -> w3
        var res2 = await service.RotateNextAsync();
        Assert.True(res2.Success);
        Assert.Equal("w3", appliedId);

        // Next -> wraps around to w1
        var res3 = await service.RotateNextAsync();
        Assert.True(res3.Success);
        Assert.Equal("w1", appliedId);
    }

    // 3. Favorites only: only items with IsFavorite == true are rotated
    [Fact]
    public async Task Scenario03_FavoritesOnly_ExcludesNonFavorites()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1", isFavorite: true),
            CreateItem("w2", "Wallpaper 2", isFavorite: false),
            CreateItem("w3", "Wallpaper 3", isFavorite: true)
        };

        var appliedIds = new List<string>();
        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                appliedIds.Add(item.Id);
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("Favorites", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // Status shows total eligible = 2
        var status = service.GetStatus();
        Assert.Equal(2, status.TotalEligibleCount);

        // Next -> w3 (skips w2)
        var res1 = await service.RotateNextAsync();
        Assert.True(res1.Success);
        Assert.Equal("w3", res1.Item?.Id);

        // Next -> wraps to w1
        var res2 = await service.RotateNextAsync();
        Assert.True(res2.Success);
        Assert.Equal("w1", res2.Item?.Id);
    }

    // 4. Missing skipped: missing files excluded from eligible candidates
    [Fact]
    public async Task Scenario04_MissingSkipped_ExcludesMissingFilesFromRotation()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1", existsOnDisk: true),
            CreateItem("w2", "Wallpaper 2", existsOnDisk: false), // missing
            CreateItem("w3", "Wallpaper 3", existsOnDisk: true)
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("All", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        var status = service.GetStatus();
        Assert.Equal(2, status.TotalEligibleCount);

        // Next jumps directly to w3
        var res = await service.RotateNextAsync();
        Assert.True(res.Success);
        Assert.Equal("w3", res.Item?.Id);
    }

    // 5. Shuffle immediate repeat avoided: full cycle completed before repeat
    [Fact]
    public async Task Scenario05_Shuffle_ImmediateRepeatAvoided()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2"),
            CreateItem("w3", "Wallpaper 3")
        };

        var applied = new List<string>();
        var testRandom = new TestRandomProvider(0, 0, 0, 0, 0, 0);

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                applied.Add(item.Id);
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: ct => Task.FromResult(OkResponse("status")),
            randomProvider: testRandom);

        await service.UpdateSettingsAsync("All", "Shuffle", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        var r1 = await service.RotateNextAsync();
        var r2 = await service.RotateNextAsync();
        var r3 = await service.RotateNextAsync();

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.True(r3.Success);

        Assert.NotEqual(r1.Item?.Id, r2.Item?.Id);
        Assert.NotEqual(r2.Item?.Id, r3.Item?.Id);
    }

    // 6. Next: works even if rotation is Off
    [Fact]
    public async Task Scenario06_Next_WorksWhenRotationIsOff()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("Off", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        var res = await service.RotateNextAsync(trigger: "Next");
        Assert.True(res.Success);
        Assert.Equal("w2", res.Item?.Id);
        Assert.Equal("Next", service.History[0].Trigger);
    }

    // 7. Previous: navigates backward and wraps around, works when Off
    [Fact]
    public async Task Scenario07_Previous_NavigatesBackwardAndWrapsAround()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2"),
            CreateItem("w3", "Wallpaper 3")
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("Off", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // From w1 (index 0), Previous wraps to w3 (index 2)
        var res1 = await service.RotatePreviousAsync(trigger: "Previous");
        Assert.True(res1.Success);
        Assert.Equal("w3", res1.Item?.Id);

        // From w3, Previous moves to w2
        var res2 = await service.RotatePreviousAsync(trigger: "Previous");
        Assert.True(res2.Success);
        Assert.Equal("w2", res2.Item?.Id);
    }

    // 8. Auto interval triggers once and updates next due time
    [Fact]
    public async Task Scenario08_AutoInterval_TriggersOnceAndUpdatesNextDueTime()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("All", "Sequential", 5);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        var statusBefore = service.GetStatus();
        Assert.True(statusBefore.NextDueTimeUtc.HasValue);

        // Simulate auto timer firing
        var res = await service.RotateNextAsync(trigger: "Auto");
        Assert.True(res.Success);
        Assert.Equal("w2", res.Item?.Id);
        Assert.Equal("Auto", service.History[0].Trigger);

        var statusAfter = service.GetStatus();
        Assert.True(statusAfter.NextDueTimeUtc > DateTimeOffset.UtcNow);
    }

    // 9. Paused reason blocks rotation and marks IsOverdue
    [Fact]
    public async Task Scenario09_PausedReason_BlocksRotationAndMarksOverdue()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var applyCalled = false;
        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                applyCalled = true;
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            // Host is currently paused due to Fullscreen
            getHostStatus: ct => Task.FromResult(new IpcResponse
            {
                Success = true,
                HasActiveWallpaper = true,
                IsPaused = true,
                PauseReasons = "Fullscreen"
            }));

        await service.UpdateSettingsAsync("All", "Sequential", 15);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // Auto trigger during pause
        var res = await service.RotateNextAsync(trigger: "Auto");
        Assert.False(res.Success);
        Assert.True(res.Skipped);
        Assert.False(applyCalled);
        Assert.True(service.IsOverdue);

        var status = service.GetStatus();
        Assert.True(status.IsOverdue);
        Assert.True(status.IsPaused);
        Assert.Equal("Fullscreen", status.PauseReasons);
    }

    // 10. Resume performs one overdue rotate
    [Fact]
    public async Task Scenario10_Resume_PerformsOneOverdueRotate()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var isPaused = true;
        var appliedCount = 0;
        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                appliedCount++;
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: ct => Task.FromResult(new IpcResponse
            {
                Success = true,
                HasActiveWallpaper = true,
                IsPaused = isPaused,
                PauseReasons = isPaused ? "BatterySaver" : null
            }));

        await service.UpdateSettingsAsync("All", "Sequential", 15);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // Trigger auto while paused -> overdue
        await service.RotateNextAsync(trigger: "Auto");
        Assert.True(service.IsOverdue);
        Assert.Equal(0, appliedCount);

        // Host resumes
        isPaused = false;
        await service.CheckOverdueOnResumeAsync();

        Assert.Equal(1, appliedCount);
        Assert.False(service.IsOverdue);
        Assert.Equal("w2", service.CurrentItemId);
    }

    // 11. Manual pause + Next keeps paused semantics
    [Fact]
    public async Task Scenario11_ManualPausePlusNext_KeepsPausedSemantics()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var pauseHostCalled = false;
        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(new IpcResponse
            {
                Success = true,
                HasActiveWallpaper = true,
                IsPaused = true,
                PauseReasons = "Manual"
            }),
            pauseHost: ct =>
            {
                pauseHostCalled = true;
                return Task.FromResult(OkResponse("paused"));
            });

        await service.UpdateSettingsAsync("All", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // User manually clicks Next while wallpaper is paused
        var res = await service.RotateNextAsync(trigger: "Next");
        Assert.True(res.Success);
        Assert.Equal("w2", res.Item?.Id);
        // Pause was re-applied to new wallpaper
        Assert.True(pauseHostCalled);
    }

    // 12. Apply failure skips safely to next candidate
    [Fact]
    public async Task Scenario12_ApplyFailure_SkipsSafelyToNextCandidate()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2"), // fails
            CreateItem("w3", "Wallpaper 3")  // succeeds
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                if (item.Id == "w2")
                {
                    return Task.FromResult(FailureResult("Corrupt file"));
                }
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("All", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // Rotating next encounters w2 failure, safely skips to w3
        var res = await service.RotateNextAsync();
        Assert.True(res.Success);
        Assert.Equal("w3", res.Item?.Id);
    }

    // 13. All candidates fail -> safe stop without infinite loop
    [Fact]
    public async Task Scenario13_AllCandidatesFail_SafelyStopsWithoutInfiniteLoop()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var attempts = 0;
        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                attempts++;
                return Task.FromResult(FailureResult("Simulated render crash"));
            },
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("All", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        var res = await service.RotateNextAsync();
        Assert.False(res.Success);
        Assert.True(res.Stopped);
        // Attempts should be capped at candidate count
        Assert.InRange(attempts, 1, items.Count);
    }

    // 14. Settings backward compatibility
    [Fact]
    public async Task Scenario14_SettingsBackwardCompatibility_LoadsMissingRotationFields()
    {
        var settingsJson = """
        {
            "RestoreLastWallpaper": true,
            "LastWallpaperType": "Image",
            "LastWallpaperPath": "C:\\Wallpaper.png"
        }
        """;

        var settingsPath = Path.Combine(_tempDir, "legacy_settings.json");
        await File.WriteAllTextAsync(settingsPath, settingsJson);

        var settingsService = new SettingsService(settingsPath);
        var loaded = await settingsService.LoadAsync();

        Assert.Equal("Off", loaded.RotationMode);
        Assert.Equal("Sequential", loaded.RotationOrder);
        Assert.Equal(30, loaded.RotationIntervalMinutes);
        Assert.Null(loaded.LastRotationItemId);
    }

    // 15. Tray/App hidden rotation continues independently
    [Fact]
    public async Task Scenario15_TrayAppHidden_RotationOperatesIndependently()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2")
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("All", "Sequential", 15);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // Rotation service has its own timer, next due time is valid even with no UI window
        var status = service.GetStatus();
        Assert.True(status.IsActive);
        Assert.NotNull(status.NextDueTimeUtc);
    }

    // 16. Sleep/resume overdue single rotate: multiple overdue intervals yield at most 1 rotate
    [Fact]
    public async Task Scenario16_SleepResume_MultipleOverdueIntervalsYieldAtMostOneRotate()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2"),
            CreateItem("w3", "Wallpaper 3")
        };

        var rotateCount = 0;
        var isPaused = true;
        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) =>
            {
                rotateCount++;
                return Task.FromResult(SuccessResult(item.FilePath));
            },
            getHostStatus: ct => Task.FromResult(new IpcResponse
            {
                Success = true,
                HasActiveWallpaper = true,
                IsPaused = isPaused
            }));

        await service.UpdateSettingsAsync("All", "Sequential", 5);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        // Multiple timer ticks occur during sleep (isPaused = true)
        await service.RotateNextAsync(trigger: "Auto");
        await service.RotateNextAsync(trigger: "Auto");
        await service.RotateNextAsync(trigger: "Auto");

        Assert.True(service.IsOverdue);
        Assert.Equal(0, rotateCount);

        // System resumes
        isPaused = false;
        await service.CheckOverdueOnResumeAsync();

        // Exactly 1 rotation occurs upon resume
        Assert.Equal(1, rotateCount);
        Assert.False(service.IsOverdue);
    }

    // 17. Remove active candidate safe: gracefully handles removal of active item
    [Fact]
    public async Task Scenario17_RemoveActiveCandidate_GracefullySelectsValidCandidate()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1"),
            CreateItem("w2", "Wallpaper 2"),
            CreateItem("w3", "Wallpaper 3")
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("All", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[1].FilePath, items[1].Id); // w2 active

        // User removes w2 from library
        items.RemoveAt(1);
        service.NotifyLibraryChanged();

        var status = service.GetStatus();
        Assert.Equal(2, status.TotalEligibleCount);

        // Next rotates safely to a valid candidate
        var res = await service.RotateNextAsync();
        Assert.True(res.Success);
        Assert.NotNull(res.Item);
    }

    // 18. Favorite eligibility update safe: toggling favorite dynamically updates eligible count
    [Fact]
    public async Task Scenario18_FavoriteEligibilityUpdate_DynamicallyUpdatesCandidates()
    {
        var items = new List<WallpaperLibraryItem>
        {
            CreateItem("w1", "Wallpaper 1", isFavorite: true),
            CreateItem("w2", "Wallpaper 2", isFavorite: false)
        };

        var service = new WallpaperRotationService(
            getItems: () => items,
            applyItem: (item, ct) => Task.FromResult(SuccessResult(item.FilePath)),
            getHostStatus: ct => Task.FromResult(OkResponse("status")));

        await service.UpdateSettingsAsync("Favorites", "Sequential", 30);
        service.NotifyCurrentWallpaper(items[0].FilePath, items[0].Id);

        var status1 = service.GetStatus();
        Assert.Equal(1, status1.TotalEligibleCount);

        // User favorites w2
        items[1].IsFavorite = true;
        service.NotifyLibraryChanged();

        var status2 = service.GetStatus();
        Assert.Equal(2, status2.TotalEligibleCount);

        // Next now rotates to newly favored w2
        var res = await service.RotateNextAsync();
        Assert.True(res.Success);
        Assert.Equal("w2", res.Item?.Id);
    }
}
