using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;
using NiceWP.Infrastructure.Localization;
using NiceWP.Infrastructure.Observatory;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class ResourceGuardStatusExposureTests
{
    [Fact]
    public void SnapshotAndIpcResponse_ExposeResourceGuardStatus()
    {
        var snapshot = new RuntimePerformanceSnapshot
        {
            ResourceGuardActive = true
        };

        var perfStatus = new RuntimePerformanceStatus
        {
            Snapshot = snapshot
        };

        var response = IpcResponse.PerformanceStatus("req-1", perfStatus);

        Assert.True(response.ResourceGuardActive);
    }

    [Fact]
    public void PerformanceSnapshotFormatter_FormatsResourceGuardStatus()
    {
        var activeEn = PerformanceSnapshotFormatter.FormatResourceGuardStatus(true, new FakeLoc(EffectiveLanguage.English));
        Assert.Equal("Active", activeEn);

        var standbyEn = PerformanceSnapshotFormatter.FormatResourceGuardStatus(false, new FakeLoc(EffectiveLanguage.English));
        Assert.Equal("Standby", standbyEn);

        var activeTr = PerformanceSnapshotFormatter.FormatResourceGuardStatus(true, new FakeLoc(EffectiveLanguage.Turkish));
        Assert.Equal("Aktif", activeTr);

        var standbyTr = PerformanceSnapshotFormatter.FormatResourceGuardStatus(false, new FakeLoc(EffectiveLanguage.Turkish));
        Assert.Equal("Beklemede", standbyTr);
    }

    private sealed class FakeLoc : ILocalizationService
    {
        public EffectiveLanguage CurrentLanguage { get; }
        public event Action<EffectiveLanguage>? LanguageChanged { add { } remove { } }
        public FakeLoc(EffectiveLanguage lang) => CurrentLanguage = lang;
        public string GetString(string key) => key;
        public string Format(string key, params object[] args) => string.Format(key, args);
        public void SetLanguage(EffectiveLanguage language) { }
    }
}
