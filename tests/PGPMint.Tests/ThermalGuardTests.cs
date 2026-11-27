using PGPMint.Core.Search;
using PGPMint.Core.Storage;
using Xunit;

namespace PGPMint.Tests;

public sealed class ThermalGuardTests
{
    [Fact]
    public void NormalizeLimitAcceptsValidRangeAndRejectsOutOfRange()
    {
        Assert.Null(ThermalGuardController.NormalizeLimit(null));
        Assert.Null(ThermalGuardController.NormalizeLimit(0));
        Assert.Null(ThermalGuardController.NormalizeLimit(64));
        Assert.Null(ThermalGuardController.NormalizeLimit(91));
        Assert.Equal(65, ThermalGuardController.NormalizeLimit(65));
        Assert.Equal(80, ThermalGuardController.NormalizeLimit(80));
        Assert.Equal(90, ThermalGuardController.NormalizeLimit(90));
    }

    [Fact]
    public void UpdateTrimsRateAfterHoldHoldsDeadbandAndRecoversBelowHysteresis()
    {
        var guard = new ThermalGuardController();
        var hot = new GpuSensorSample(true, 300, 300, true, 100, 100, 0)
        {
            HasTemperature = true,
            TemperatureCelsius = 83
        };

        // Within initial 1.0s hold: still at 1.0
        Assert.Equal(1.0, guard.Update(80, hot, 0.5));
        Assert.False(guard.IsThrottling);

        // Exceeds hold: starts trimming smoothly
        double s1 = guard.Update(80, hot, 0.6);
        Assert.True(s1 < 1.0 && s1 >= 0.08);
        Assert.True(guard.IsThrottling);

        // Inside deadband (78..80 for limit=80): stays steady without bouncing up
        var deadband = hot with { TemperatureCelsius = 79 };
        double sDead = guard.Update(80, deadband, 0.5);
        Assert.Equal(s1, sDead, precision: 6);

        // Below hysteresis (<= 77C) after recovery hold (2.0s): recovers smoothly back to 1.0
        var cool = hot with { TemperatureCelsius = 76 };
        Assert.Equal(s1, guard.Update(80, cool, 1.5), precision: 6);
        double sRecover = guard.Update(80, cool, 1.0);
        Assert.True(sRecover > s1);

        for (int i = 0; i < 20; i++)
        {
            guard.Update(80, cool, 0.5);
        }

        Assert.Equal(1.0, guard.ThermalScale);
        Assert.False(guard.IsThrottling);
    }

    [Fact]
    public void EmergencyOffsetPausesGpuAndDisablingLimitResetsImmediately()
    {
        var guard = new ThermalGuardController();
        var extreme = new GpuSensorSample(false, 0, 0, false, 0, 0, 0)
        {
            HasTemperature = true,
            TemperatureCelsius = 88
        };

        Assert.Equal(0.0, guard.Update(80, extreme, 0.25));
        Assert.True(guard.IsThrottling);

        Assert.Equal(1.0, guard.Update(null, extreme, 0.25));
        Assert.False(guard.IsThrottling);
    }

    [Fact]
    public void RateControlAppliesThermalScaleToMaxAndSubFullTiers()
    {
        RateDirective maxTrimmed = RateControl.FromTier(PowerTier.Max, 10_000_000, profile: null, yieldScale: 1.0, thermalScale: 0.6);
        Assert.Equal(RateMode.Capped, maxTrimmed.Mode);
        Assert.Equal(6_000_000L, maxTrimmed.TargetHashesPerSecond);

        RateDirective paused = RateControl.FromTier(PowerTier.Efficiency, 10_000_000, profile: null, yieldScale: 1.0, thermalScale: 0.0);
        Assert.Equal(RateMode.Paused, paused.Mode);

        RateDirective ecoCombined = RateControl.FromTier(PowerTier.Eco, 10_000_000, profile: null, yieldScale: 0.5, thermalScale: 0.5);
        Assert.Equal(RateMode.Capped, ecoCombined.Mode);
        Assert.Equal(250_000L, ecoCombined.TargetHashesPerSecond); // 10M * 0.10 * 0.5 * 0.5
    }

    [Fact]
    public async Task MultiGpuCoordinatorIsolatesThermalGuardPerGpu()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pgpmint-thermal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dev0 = new GpuDeviceInfo("gpu:0", 0, "Hot GPU 0", 0x10DE, 0x2204, 0, 0xA1, 24UL << 30, "595.99", true, "Fake", "fake-thermal-v1");
            var dev1 = new GpuDeviceInfo("gpu:1", 0, "Cool GPU 1", 0x10DE, 0x2204, 0, 0xA1, 24UL << 30, "595.99", true, "Fake", "fake-thermal-v1");
            var backend = new StaticTwoGpuBackend([dev0, dev1]);
            var sensor = new PerDeviceTempSensor(dev0.DeviceKey, temp0: 85.0, dev1.DeviceKey, temp1: 65.0);

            char[] pw = "test-pw".ToCharArray();
            using var protector = PasswordSeedProtector.Open(dir, pw);
            Array.Clear(pw);
            var stateStore = new AppStateStore(dir);
            var vaultStore = new VaultStore(dir, protector);

            using var coord = new MultiGpuCoordinator(
                backend,
                stateStore,
                vaultStore,
                new CoordinatorOptions
                {
                    FullCalibrationDuration = TimeSpan.FromMilliseconds(250),
                    CalibrationWindow = TimeSpan.FromMilliseconds(150),
                    SensorPollInterval = TimeSpan.FromMilliseconds(50),
                    TelemetryInterval = TimeSpan.FromMilliseconds(50)
                },
                sensor);

            coord.Initialize();
            coord.SetMaxGpuTemperatureCelsius(75);
            Assert.Equal(75, coord.MaxGpuTemperatureCelsius);
            Assert.Equal(75, stateStore.Load().MaxGpuTemperatureCelsius);

            await coord.StartSearchAsync(KeyIdPattern.Parse("BEEF"));
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < deadline)
            {
                CoordinatorTelemetry snap = coord.LatestTelemetry;
                if (snap.State == CoordinatorState.Running &&
                    snap.Devices.FirstOrDefault(d => d.DeviceKey == dev0.DeviceKey)?.IsThermalGuardActive == true)
                {
                    break;
                }
                await Task.Delay(50);
            }

            CoordinatorTelemetry finalSnap = coord.LatestTelemetry;
            await coord.StopAsync();

            GpuTelemetrySnapshot snap0 = finalSnap.Devices.Single(d => d.DeviceKey == dev0.DeviceKey);
            GpuTelemetrySnapshot snap1 = finalSnap.Devices.Single(d => d.DeviceKey == dev1.DeviceKey);
            Assert.True(snap0.IsThermalGuardActive);
            Assert.True(snap0.IsThermalThrottled);
            Assert.Equal(85.0, snap0.TemperatureCelsius);
            Assert.False(snap1.IsThermalGuardActive);
            Assert.False(snap1.IsThermalThrottled);
            Assert.Equal(65.0, snap1.TemperatureCelsius);
            Assert.True(finalSnap.IsThermalGuardActive);
            Assert.Equal(85.0, finalSnap.MaxTemperatureCelsius);
            Assert.Equal(75, finalSnap.MaxGpuTemperatureLimitCelsius);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    private sealed class PerDeviceTempSensor(string key0, double temp0, string key1, double temp1) : IGpuTelemetrySensor
    {
        public bool HasPowerSensor(GpuDeviceInfo device) => true;

        public GpuSensorSample Sample(GpuDeviceInfo device)
        {
            bool is0 = string.Equals(device.DeviceKey, key0, StringComparison.Ordinal);
            double t = string.Equals(device.DeviceKey, key1, StringComparison.Ordinal) ? temp1 : (is0 ? temp0 : temp1);
            return new GpuSensorSample(true, is0 ? 280.0 : 250.0, 300.0, true, 100.0, 100.0, 0.0)
            {
                HasTemperature = true,
                TemperatureCelsius = t,
                IsThermalThrottled = is0,
                IsPowerCapped = true
            };
        }

        public void Dispose() { }
    }

    private sealed class StaticTwoGpuBackend(IReadOnlyList<GpuDeviceInfo> devs) : IGpuBackend
    {
        public IReadOnlyList<GpuDeviceInfo> EnumerateHardwareGpus() => devs;
        public IGpuSearchDevice CreateDevice(GpuDeviceInfo info) => new ZeroHitFakeDevice(info);
    }

    private sealed class ZeroHitFakeDevice(GpuDeviceInfo info) : IGpuSearchDevice
    {
        public GpuDeviceInfo Info { get; } = info;
        public int KeysPerBatch => 4;
        public long MaxCandidatesPerBatch => 4096;
        public string KernelVersion => "fake-thermal-v1";

        public IReadOnlyList<uint>[] ScanGroup(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern, int capacity = 1024) =>
            Enumerable.Range(0, publicKeys.Count).Select(_ => (IReadOnlyList<uint>)Array.Empty<uint>()).ToArray();

        public SearchBatchResult ScanBatch(IReadOnlyList<byte[]> publicKeys, uint startTimestamp, uint timestampCount, KeyIdPattern pattern, int capacity = 1024, SearchResultMode resultMode = SearchResultMode.FindOne, CancellationToken cancellationToken = default)
        {
            Thread.Sleep(5);
            long total = (long)timestampCount * KeysPerBatch;
            return new SearchBatchResult(
                Enumerable.Range(0, publicKeys.Count).Select(_ => (IReadOnlyList<uint>)Array.Empty<uint>()).ToArray(),
                total,
                total)
            {
                HashRangeFullyProcessed = true,
                ResultsComplete = true,
                DispatchWallMilliseconds = 5.0
            };
        }

        public void Dispose() { }
    }
}
