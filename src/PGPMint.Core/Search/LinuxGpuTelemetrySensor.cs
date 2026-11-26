using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace PGPMint.Core.Search;

/// <summary>
/// Zero-dependency Linux NVIDIA GPU telemetry sensor powered by libnvidia-ml.so.1 (NVML):
/// - Matches GPU devices across Vulkan and OpenCL backends by normalized device UUID,
///   PCI domain/bus/device address, PCI vendor/device IDs, or single-GPU fallback.
/// - Samples live board power (watts), enforced/management power limit (watts),
///   and SM/GPU core utilization (%) for PGPMint's 30s power curve and MH/W telemetry.
/// </summary>
public sealed unsafe class LinuxGpuTelemetrySensor : IGpuTelemetrySensor
{
    private const uint NvidiaVendorId = 0x10DEu;
    private const int NvmlUuidBufferBytes = 96;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlPciInfo
    {
        public fixed byte BusIdLegacy[16];
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        public fixed byte BusId[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    internal readonly record struct NvmlAdapterIdentity(
        uint Domain,
        uint Bus,
        uint Device,
        uint PciDeviceId,
        uint PciSubSystemId,
        string? NormalizedUuid);

    private readonly record struct NvmlAdapterEntry(
        nint Handle,
        NvmlAdapterIdentity Identity,
        double PowerLimitWatts);

    private readonly object _gate = new();
    private nint _library;
    private bool _initialized;
    private bool _triedLoad;
    private delegate* unmanaged[Cdecl]<int> _nvmlShutdown;
    private delegate* unmanaged[Cdecl]<nint, uint*, int> _getPowerUsage;
    private delegate* unmanaged[Cdecl]<nint, NvmlUtilization*, int> _getUtilizationRates;
    private readonly List<NvmlAdapterEntry> _adapters = new();
    private readonly List<NvmlAdapterIdentity> _identities = new();
    private readonly Dictionary<string, int> _deviceIndexCache = new(StringComparer.Ordinal);
    private bool _disposed;

    public bool HasPowerSensor(GpuDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (_disposed || device.VendorId != NvidiaVendorId)
            {
                return false;
            }

            EnsureLoadedLocked();
            if (!_initialized || _getPowerUsage == null)
            {
                return false;
            }

            int idx = ResolveIndexLocked(device);
            if (idx < 0)
            {
                return false;
            }

            uint mw = 0;
            return _getPowerUsage(_adapters[idx].Handle, &mw) == 0 && mw > 0;
        }
    }

    public GpuSensorSample Sample(GpuDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (_disposed || device.VendorId != NvidiaVendorId)
            {
                return GpuSensorSample.Empty;
            }

            EnsureLoadedLocked();
            if (!_initialized)
            {
                return GpuSensorSample.Empty;
            }

            int idx = ResolveIndexLocked(device);
            if (idx < 0)
            {
                return GpuSensorSample.Empty;
            }

            NvmlAdapterEntry entry = _adapters[idx];
            bool hasPower = false;
            double watts = 0.0;
            if (_getPowerUsage != null)
            {
                uint mw = 0;
                if (_getPowerUsage(entry.Handle, &mw) == 0 && mw > 0)
                {
                    hasPower = true;
                    watts = mw / 1000.0;
                }
            }

            bool hasUtil = false;
            double gpuUtil = 0.0;
            if (_getUtilizationRates != null)
            {
                NvmlUtilization util = default;
                if (_getUtilizationRates(entry.Handle, &util) == 0)
                {
                    hasUtil = true;
                    gpuUtil = Math.Clamp((double)util.Gpu, 0.0, 100.0);
                }
            }

            if (!hasPower && !hasUtil)
            {
                return GpuSensorSample.Empty;
            }

            return new GpuSensorSample(
                HasPowerWatts: hasPower,
                PowerWatts: watts,
                PowerLimitWatts: entry.PowerLimitWatts,
                HasGpuUtil: hasUtil,
                TotalGpuUtil: gpuUtil,
                SelfGpuUtil: gpuUtil,
                OtherProcessGpuUtil: 0.0);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_initialized && _nvmlShutdown != null)
            {
                try { _nvmlShutdown(); } catch { }
            }

            _initialized = false;
            _adapters.Clear();
            _identities.Clear();
            _deviceIndexCache.Clear();
            ReleaseLibraryLocked();
        }
    }

    internal static string? NormalizeNvmlUuid(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        ReadOnlySpan<char> span = raw.AsSpan().Trim();
        if (span.StartsWith("GPU-", StringComparison.OrdinalIgnoreCase) ||
            span.StartsWith("MIG-", StringComparison.OrdinalIgnoreCase))
        {
            span = span[4..];
        }

        Span<char> hex = stackalloc char[32];
        int written = 0;
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '-')
            {
                continue;
            }

            if (!char.IsAsciiHexDigit(c) || written >= 32)
            {
                return null;
            }

            hex[written++] = char.ToLowerInvariant(c);
        }

        return written == 32 ? new string(hex) : null;
    }

    internal static bool TryParsePciAddress(string? pciAddress, out uint domain, out uint bus, out uint device)
    {
        domain = 0;
        bus = 0;
        device = 0;
        if (string.IsNullOrWhiteSpace(pciAddress))
        {
            return false;
        }

        string s = pciAddress.Trim();
        if (s.StartsWith("pci:", StringComparison.OrdinalIgnoreCase))
        {
            s = s[4..].Trim();
        }

        string[] parts = s.Split(':');
        if (parts.Length != 3)
        {
            return false;
        }

        string devPart = parts[2];
        int dotIdx = devPart.IndexOf('.');
        if (dotIdx >= 0)
        {
            if (devPart.IndexOf('.', dotIdx + 1) >= 0)
            {
                return false;
            }

            string fnPart = devPart[(dotIdx + 1)..];
            if (fnPart.Length == 0 || !uint.TryParse(fnPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            devPart = devPart[..dotIdx];
        }

        return parts[0].Length > 0 &&
               parts[1].Length > 0 &&
               devPart.Length > 0 &&
               uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out domain) &&
               uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bus) &&
               uint.TryParse(devPart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out device);
    }

    internal static int ResolveAdapterIndex(GpuDeviceInfo device, IReadOnlyList<NvmlAdapterIdentity> adapters)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(adapters);

        if (device.VendorId != NvidiaVendorId || adapters.Count == 0)
        {
            return -1;
        }

        // 1. Exact device UUID match (Vulkan VK_KHR_external_memory / OpenCL cl_khr_device_uuid)
        string? rawUuid = device.DeviceUuid;
        if (string.IsNullOrWhiteSpace(rawUuid) &&
            device.DeviceKey.StartsWith("uuid:", StringComparison.OrdinalIgnoreCase))
        {
            rawUuid = device.DeviceKey[5..];
        }

        if (NormalizeNvmlUuid(rawUuid) is { } targetUuid)
        {
            for (int i = 0; i < adapters.Count; i++)
            {
                if (string.Equals(adapters[i].NormalizedUuid, targetUuid, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }
        }

        // 2. Exact PCI domain/bus/device match
        string? rawPci = device.PciAddress;
        if (string.IsNullOrWhiteSpace(rawPci) &&
            device.DeviceKey.StartsWith("pci:", StringComparison.OrdinalIgnoreCase))
        {
            rawPci = device.DeviceKey;
        }

        if (TryParsePciAddress(rawPci, out uint domain, out uint bus, out uint devNum))
        {
            for (int i = 0; i < adapters.Count; i++)
            {
                NvmlAdapterIdentity a = adapters[i];
                if (a.Domain == domain && a.Bus == bus && a.Device == devNum)
                {
                    return i;
                }
            }
        }

        // 3. PCI vendor + device ID (+ subsystem ID) + optional ordinal fallback
        if (device.VendorDeviceId != 0)
        {
            uint expectedPciDevId = (device.VendorDeviceId << 16) | (device.VendorId & 0xFFFFu);
            int ordinal = ParseDeviceOrdinal(device.DeviceKey);

            var exactSubSys = new List<int>();
            var byDevId = new List<int>();
            for (int i = 0; i < adapters.Count; i++)
            {
                if (adapters[i].PciDeviceId != expectedPciDevId)
                {
                    continue;
                }

                byDevId.Add(i);
                if (device.SubSysId == 0 || adapters[i].PciSubSystemId == device.SubSysId)
                {
                    exactSubSys.Add(i);
                }
            }

            if (exactSubSys.Count > 0)
            {
                return exactSubSys[Math.Clamp(ordinal, 0, exactSubSys.Count - 1)];
            }

            if (byDevId.Count > 0)
            {
                return byDevId[Math.Clamp(ordinal, 0, byDevId.Count - 1)];
            }
        }

        // 4. Unambiguous single NVIDIA GPU fallback
        return adapters.Count == 1 ? 0 : -1;
    }

    private int ResolveIndexLocked(GpuDeviceInfo device)
    {
        if (_deviceIndexCache.TryGetValue(device.DeviceKey, out int cached))
        {
            return cached;
        }

        int resolved = ResolveAdapterIndex(device, _identities);
        _deviceIndexCache[device.DeviceKey] = resolved;
        return resolved;
    }

    private void EnsureLoadedLocked()
    {
        if (_triedLoad)
        {
            return;
        }

        _triedLoad = true;
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        if (!TryLoadNvmlLibrary(out nint lib))
        {
            return;
        }

        _library = lib;
        try
        {
            if (!NativeLibrary.TryGetExport(lib, "nvmlInit_v2", out nint initPtr) ||
                !NativeLibrary.TryGetExport(lib, "nvmlShutdown", out nint shutdownPtr) ||
                !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetCount_v2", out nint countPtr) ||
                !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetHandleByIndex_v2", out nint handlePtr) ||
                !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetPowerUsage", out nint powerPtr) ||
                !NativeLibrary.TryGetExport(lib, "nvmlDeviceGetPciInfo_v3", out nint pciPtr))
            {
                ReleaseLibraryLocked();
                return;
            }

            var init = (delegate* unmanaged[Cdecl]<int>)initPtr;
            _nvmlShutdown = (delegate* unmanaged[Cdecl]<int>)shutdownPtr;
            var getCount = (delegate* unmanaged[Cdecl]<uint*, int>)countPtr;
            var getHandle = (delegate* unmanaged[Cdecl]<uint, nint*, int>)handlePtr;
            _getPowerUsage = (delegate* unmanaged[Cdecl]<nint, uint*, int>)powerPtr;
            var getPci = (delegate* unmanaged[Cdecl]<nint, NvmlPciInfo*, int>)pciPtr;

            _getUtilizationRates = NativeLibrary.TryGetExport(lib, "nvmlDeviceGetUtilizationRates", out nint utilPtr)
                ? (delegate* unmanaged[Cdecl]<nint, NvmlUtilization*, int>)utilPtr
                : null;
            delegate* unmanaged[Cdecl]<nint, byte*, uint, int> getUuid =
                NativeLibrary.TryGetExport(lib, "nvmlDeviceGetUUID", out nint uuidPtr)
                    ? (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)uuidPtr
                    : null;
            delegate* unmanaged[Cdecl]<nint, uint*, int> getEnforcedLimit =
                NativeLibrary.TryGetExport(lib, "nvmlDeviceGetEnforcedPowerLimit", out nint enforcedPtr)
                    ? (delegate* unmanaged[Cdecl]<nint, uint*, int>)enforcedPtr
                    : null;
            delegate* unmanaged[Cdecl]<nint, uint*, int> getMgmtLimit =
                NativeLibrary.TryGetExport(lib, "nvmlDeviceGetPowerManagementLimit", out nint mgmtPtr)
                    ? (delegate* unmanaged[Cdecl]<nint, uint*, int>)mgmtPtr
                    : null;

            if (init() != 0)
            {
                ReleaseLibraryLocked();
                return;
            }

            _initialized = true;
            uint count = 0;
            if (getCount(&count) != 0 || count == 0 || count > 64)
            {
                return;
            }

            byte* uuidBuf = stackalloc byte[NvmlUuidBufferBytes];
            for (uint i = 0; i < count; i++)
            {
                nint h = 0;
                if (getHandle(i, &h) != 0 || h == 0)
                {
                    continue;
                }

                NvmlPciInfo pci = default;
                if (getPci(h, &pci) != 0)
                {
                    continue;
                }

                string? normalizedUuid = null;
                if (getUuid != null && getUuid(h, uuidBuf, NvmlUuidBufferBytes) == 0)
                {
                    int len = 0;
                    while (len < NvmlUuidBufferBytes && uuidBuf[len] != 0)
                    {
                        len++;
                    }

                    if (len > 0)
                    {
                        normalizedUuid = NormalizeNvmlUuid(Encoding.ASCII.GetString(uuidBuf, len));
                    }
                }

                double limitWatts = 0.0;
                uint limitMw = 0;
                if (getEnforcedLimit != null && getEnforcedLimit(h, &limitMw) == 0 && limitMw > 0)
                {
                    limitWatts = limitMw / 1000.0;
                }
                else if (getMgmtLimit != null && getMgmtLimit(h, &limitMw) == 0 && limitMw > 0)
                {
                    limitWatts = limitMw / 1000.0;
                }

                var identity = new NvmlAdapterIdentity(
                    pci.Domain,
                    pci.Bus,
                    pci.Device,
                    pci.PciDeviceId,
                    pci.PciSubSystemId,
                    normalizedUuid);
                _identities.Add(identity);
                _adapters.Add(new NvmlAdapterEntry(h, identity, limitWatts));
            }
        }
        catch
        {
            if (_initialized && _nvmlShutdown != null)
            {
                try { _nvmlShutdown(); } catch { }
            }

            _initialized = false;
            _adapters.Clear();
            _identities.Clear();
            _deviceIndexCache.Clear();
            ReleaseLibraryLocked();
        }
    }

    private static int ParseDeviceOrdinal(string deviceKey)
    {
        int lastColon = deviceKey.LastIndexOf(':');
        if (lastColon >= 0 && lastColon + 1 < deviceKey.Length &&
            int.TryParse(deviceKey.AsSpan(lastColon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
            idx >= 0)
        {
            return idx;
        }

        return 0;
    }

    private static bool TryLoadNvmlLibrary(out nint handle)
    {
        if (NativeLibrary.TryLoad("libnvidia-ml.so.1", typeof(LinuxGpuTelemetrySensor).Assembly, null, out handle))
        {
            return true;
        }

        if (NativeLibrary.TryLoad("libnvidia-ml.so", typeof(LinuxGpuTelemetrySensor).Assembly, null, out handle))
        {
            return true;
        }

        handle = 0;
        return false;
    }

    private void ReleaseLibraryLocked()
    {
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }

        _nvmlShutdown = null;
        _getPowerUsage = null;
        _getUtilizationRates = null;
    }
}
