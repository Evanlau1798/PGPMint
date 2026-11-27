using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PGPMint.Core.Search;

public readonly record struct GpuSensorSample(
    bool HasPowerWatts,
    double PowerWatts,
    double PowerLimitWatts,
    bool HasGpuUtil,
    double TotalGpuUtil,
    double SelfGpuUtil,
    double OtherProcessGpuUtil)
{
    public bool HasTemperature { get; init; }
    public double TemperatureCelsius { get; init; }
    public bool IsThermalThrottled { get; init; }
    public bool IsPowerCapped { get; init; }

    public static GpuSensorSample Empty => default;
}

public interface IGpuTelemetrySensor : IDisposable
{
    bool HasPowerSensor(GpuDeviceInfo device);
    GpuSensorSample Sample(GpuDeviceInfo device);
}

public sealed class NullGpuTelemetrySensor : IGpuTelemetrySensor
{
    public static NullGpuTelemetrySensor Instance { get; } = new();
    private NullGpuTelemetrySensor() { }
    public bool HasPowerSensor(GpuDeviceInfo device) => false;
    public GpuSensorSample Sample(GpuDeviceInfo device) => GpuSensorSample.Empty;
    public void Dispose() { }
}

/// <summary>
/// Zero-dependency, unprivileged Windows GPU telemetry sensor:
/// - Reads live board watts via NVIDIA nvml.dll (loaded from System32 first) matched by PCI Bus/Device
///   from D3DKMTQueryAdapterInfo (falling back to PCI IDs and ordinal).
/// - Reads total, self-process, and other-process GPU engine utilization via Windows native
///   D3DKMTQueryStatistics (gdi32.dll) across NVIDIA, AMD, Intel, and ARM64 WDDM adapters.
/// </summary>
public sealed class WindowsGpuTelemetrySensor : IGpuTelemetrySensor
{
    private readonly object _gate = new();
    private readonly NvmlPowerReader _nvml;
    private readonly Dictionary<long, D3dKmtAdapterSampler> _d3dSamplers = new();
    private bool _disposed;

    public WindowsGpuTelemetrySensor()
    {
        _nvml = new NvmlPowerReader();
    }

    internal static unsafe int NodeMetadataStructSize => sizeof(D3dKmtAdapterSampler.D3dKmtNodeMetadata);
    internal static int NodeMetadataMarshalSize => Marshal.SizeOf<D3dKmtAdapterSampler.D3dKmtNodeMetadata>();

    internal static bool IsComputeContendingEngine(int engineType, string? friendlyName)
    {
        // DXGK_ENGINE_TYPE from WDK d3dkmdt.h:
        // 0=OTHER, 1=3D, 2=VIDEO_DECODE, 3=VIDEO_ENCODE, 4=VIDEO_PROCESSING,
        // 5=SCENE_ASSEMBLY, 6=COPY, 7=OVERLAY, 8=CRYPTO, 9=VIDEO_CODEC
        return engineType switch
        {
            1 or 5 => true,
            2 or 3 or 4 or 6 or 7 or 8 or 9 => false,
            0 => IsOtherEngineComputeContending(friendlyName),
            _ => false
        };
    }

    private static bool IsOtherEngineComputeContending(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            return false;
        }

        string name = friendlyName.Trim();
        if (name.Contains("video", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("encode", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("decode", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("copy", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("overlay", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("security", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("crypto", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ofa", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("optical", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return name.Contains("compute", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("cuda", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("3d", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("graphics", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("render", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("shader", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("vr", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("vr_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("vr ", StringComparison.OrdinalIgnoreCase);
    }

    public bool HasPowerSensor(GpuDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            return _nvml.HasSensorFor(device);
        }
    }

    public GpuSensorSample Sample(GpuDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        lock (_gate)
        {
            if (_disposed)
            {
                return GpuSensorSample.Empty;
            }

            bool hasAnyNvml = _nvml.TrySampleTelemetry(
                device,
                out bool hasPower,
                out double watts,
                out double powerLimitWatts,
                out bool hasTemp,
                out double tempC,
                out bool isThermalThrottled,
                out bool isPowerCapped);
            if (!hasAnyNvml)
            {
                hasPower = false;
                watts = 0.0;
                powerLimitWatts = 0.0;
                hasTemp = false;
                tempC = 0.0;
                isThermalThrottled = false;
                isPowerCapped = false;
            }

            bool hasUtil = false;
            double totalUtil = 0.0;
            double selfUtil = 0.0;
            double otherUtil = 0.0;

            if (OperatingSystem.IsWindows() && IntPtr.Size == 8 && device.AdapterLuid != 0)
            {
                if (!_d3dSamplers.TryGetValue(device.AdapterLuid, out D3dKmtAdapterSampler? sampler))
                {
                    sampler = new D3dKmtAdapterSampler(device.AdapterLuid);
                    _d3dSamplers[device.AdapterLuid] = sampler;
                }

                hasUtil = sampler.TrySample(out totalUtil, out selfUtil, out otherUtil);
            }

            return new GpuSensorSample(
                HasPowerWatts: hasPower,
                PowerWatts: watts,
                PowerLimitWatts: powerLimitWatts,
                HasGpuUtil: hasUtil,
                TotalGpuUtil: totalUtil,
                SelfGpuUtil: selfUtil,
                OtherProcessGpuUtil: otherUtil)
            {
                HasTemperature = hasTemp,
                TemperatureCelsius = tempC,
                IsThermalThrottled = isThermalThrottled,
                IsPowerCapped = isPowerCapped
            };
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
            _d3dSamplers.Clear();
            _nvml.Dispose();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtOpenAdapterFromLuid
    {
        public Win32Luid AdapterLuid;
        public uint Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtQueryAdapterInfo
    {
        public uint Handle;
        public int Type;
        public nint PrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3dKmtCloseAdapter
    {
        public uint Handle;
    }

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3dKmtOpenAdapterFromLuid arg);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3dKmtQueryAdapterInfo arg);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3dKmtCloseAdapter arg);

    private sealed unsafe class NvmlPowerReader : IDisposable
    {
        private const uint NvidiaVendorId = 0x10DEu;
        private const int KmtQaiTypeAdapterAddress = 6;
        private const uint NvmlTemperatureGpu = 0u;
        private const ulong NvmlThrottleThermalMask = 0x20UL | 0x40UL; // SwThermal | HwThermal
        private const ulong NvmlThrottleSwPowerCap = 0x4UL;

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
        private struct D3dKmtAdapterAddress
        {
            public uint BusNumber;
            public uint DeviceNumber;
            public uint FunctionNumber;
        }

        private readonly record struct NvmlAdapterEntry(
            nint Handle,
            uint Bus,
            uint Device,
            uint PciDeviceId,
            uint PciSubSystemId,
            double PowerLimitWatts);

        private nint _library;
        private bool _initialized;
        private bool _triedLoad;
        private delegate* unmanaged[Cdecl]<int> _nvmlShutdown;
        private delegate* unmanaged[Cdecl]<nint, uint*, int> _getPowerUsage;
        private delegate* unmanaged[Cdecl]<nint, uint, uint*, int> _getTemperature;
        private delegate* unmanaged[Cdecl]<nint, ulong*, int> _getClockEventReasons;
        private readonly List<NvmlAdapterEntry> _adapters = new();
        private readonly Dictionary<string, nint> _deviceHandleCache = new(StringComparer.Ordinal);

        public bool HasSensorFor(GpuDeviceInfo device)
        {
            if (device.VendorId != NvidiaVendorId)
            {
                return false;
            }

            EnsureLoaded();
            if (!_initialized)
            {
                return false;
            }

            nint handle = ResolveHandle(device);
            if (handle == 0 || _getPowerUsage == null)
            {
                return false;
            }

            uint mw = 0;
            return _getPowerUsage(handle, &mw) == 0 && mw > 0;
        }

        public bool TrySampleTelemetry(
            GpuDeviceInfo device,
            out bool hasPower,
            out double watts,
            out double powerLimitWatts,
            out bool hasTemp,
            out double tempC,
            out bool isThermalThrottled,
            out bool isPowerCapped)
        {
            hasPower = false;
            watts = 0.0;
            powerLimitWatts = 0.0;
            hasTemp = false;
            tempC = 0.0;
            isThermalThrottled = false;
            isPowerCapped = false;

            if (device.VendorId != NvidiaVendorId)
            {
                return false;
            }

            EnsureLoaded();
            if (!_initialized)
            {
                return false;
            }

            nint handle = ResolveHandle(device);
            if (handle == 0)
            {
                return false;
            }

            if (_getPowerUsage != null)
            {
                uint mw = 0;
                if (_getPowerUsage(handle, &mw) == 0 && mw > 0)
                {
                    hasPower = true;
                    watts = mw / 1000.0;
                }
            }

            if (_getTemperature != null)
            {
                uint rawTemp = 0;
                if (_getTemperature(handle, NvmlTemperatureGpu, &rawTemp) == 0 && rawTemp is > 0 and <= 150)
                {
                    hasTemp = true;
                    tempC = rawTemp;
                }
            }

            if (_getClockEventReasons != null)
            {
                ulong reasons = 0;
                if (_getClockEventReasons(handle, &reasons) == 0)
                {
                    isThermalThrottled = (reasons & NvmlThrottleThermalMask) != 0;
                    isPowerCapped = (reasons & NvmlThrottleSwPowerCap) != 0;
                }
            }

            if (!hasPower && !hasTemp)
            {
                return false;
            }

            foreach (NvmlAdapterEntry entry in _adapters)
            {
                if (entry.Handle == handle)
                {
                    powerLimitWatts = entry.PowerLimitWatts;
                    break;
                }
            }

            return true;
        }

        private void EnsureLoaded()
        {
            if (_triedLoad)
            {
                return;
            }

            _triedLoad = true;
            if (!OperatingSystem.IsWindows())
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
                    ReleaseLibrary();
                    return;
                }

                var init = (delegate* unmanaged[Cdecl]<int>)initPtr;
                _nvmlShutdown = (delegate* unmanaged[Cdecl]<int>)shutdownPtr;
                var getCount = (delegate* unmanaged[Cdecl]<uint*, int>)countPtr;
                var getHandle = (delegate* unmanaged[Cdecl]<uint, nint*, int>)handlePtr;
                _getPowerUsage = (delegate* unmanaged[Cdecl]<nint, uint*, int>)powerPtr;
                _getTemperature = NativeLibrary.TryGetExport(lib, "nvmlDeviceGetTemperature", out nint tempPtr)
                    ? (delegate* unmanaged[Cdecl]<nint, uint, uint*, int>)tempPtr
                    : null;
                if (NativeLibrary.TryGetExport(lib, "nvmlDeviceGetCurrentClocksEventReasons", out nint reasonsPtr) ||
                    NativeLibrary.TryGetExport(lib, "nvmlDeviceGetCurrentClocksThrottleReasons", out reasonsPtr))
                {
                    _getClockEventReasons = (delegate* unmanaged[Cdecl]<nint, ulong*, int>)reasonsPtr;
                }
                else
                {
                    _getClockEventReasons = null;
                }
                var getPci = (delegate* unmanaged[Cdecl]<nint, NvmlPciInfo*, int>)pciPtr;

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
                    ReleaseLibrary();
                    return;
                }

                _initialized = true;
                uint count = 0;
                if (getCount(&count) != 0 || count == 0 || count > 64)
                {
                    return;
                }

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

                    _adapters.Add(new NvmlAdapterEntry(h, pci.Bus, pci.Device, pci.PciDeviceId, pci.PciSubSystemId, limitWatts));
                }
            }
            catch
            {
                Dispose();
            }
        }

        private nint ResolveHandle(GpuDeviceInfo device)
        {
            if (_deviceHandleCache.TryGetValue(device.DeviceKey, out nint cached))
            {
                return cached;
            }

            // 1. Exact PCI Bus/Device match via WDDM AdapterLuid (unambiguous on multi-GPU identical rigs)
            if (device.AdapterLuid != 0 && TryQueryPciBusDevice(device.AdapterLuid, out uint bus, out uint devNum))
            {
                foreach (NvmlAdapterEntry entry in _adapters)
                {
                    if (entry.Bus == bus && entry.Device == devNum)
                    {
                        _deviceHandleCache[device.DeviceKey] = entry.Handle;
                        return entry.Handle;
                    }
                }
            }

            uint expectedPciDevId = (device.VendorDeviceId << 16) | (device.VendorId & 0xFFFFu);
            int ordinal = ParseDeviceOrdinal(device.DeviceKey);

            // 2. Exact vendor+device+subsystem match
            var exactSubSys = _adapters
                .Where(a => a.PciDeviceId == expectedPciDevId && (device.SubSysId == 0 || a.PciSubSystemId == device.SubSysId))
                .ToList();
            if (exactSubSys.Count > 0)
            {
                nint h = exactSubSys[Math.Clamp(ordinal, 0, exactSubSys.Count - 1)].Handle;
                _deviceHandleCache[device.DeviceKey] = h;
                return h;
            }

            // 3. Vendor+device match regardless of subsystem encoding
            var byDevId = _adapters.Where(a => a.PciDeviceId == expectedPciDevId).ToList();
            if (byDevId.Count > 0)
            {
                nint h = byDevId[Math.Clamp(ordinal, 0, byDevId.Count - 1)].Handle;
                _deviceHandleCache[device.DeviceKey] = h;
                return h;
            }

            // 4. Single NVIDIA GPU fallback
            if (_adapters.Count == 1)
            {
                nint h = _adapters[0].Handle;
                _deviceHandleCache[device.DeviceKey] = h;
                return h;
            }

            _deviceHandleCache[device.DeviceKey] = 0;
            return 0;
        }

        private static bool TryQueryPciBusDevice(long adapterLuid, out uint busNumber, out uint deviceNumber)
        {
            busNumber = 0;
            deviceNumber = 0;
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                var openArg = new D3dKmtOpenAdapterFromLuid
                {
                    AdapterLuid = new Win32Luid
                    {
                        LowPart = (uint)(adapterLuid & 0xFFFFFFFFL),
                        HighPart = (int)(adapterLuid >> 32)
                    }
                };

                if (D3DKMTOpenAdapterFromLuid(ref openArg) != 0 || openArg.Handle == 0)
                {
                    return false;
                }

                try
                {
                    D3dKmtAdapterAddress addr = default;
                    var query = new D3dKmtQueryAdapterInfo
                    {
                        Handle = openArg.Handle,
                        Type = KmtQaiTypeAdapterAddress,
                        PrivateDriverData = (nint)(&addr),
                        PrivateDriverDataSize = (uint)sizeof(D3dKmtAdapterAddress)
                    };

                    if (D3DKMTQueryAdapterInfo(ref query) != 0)
                    {
                        return false;
                    }

                    busNumber = addr.BusNumber;
                    deviceNumber = addr.DeviceNumber;
                    return true;
                }
                finally
                {
                    var closeArg = new D3dKmtCloseAdapter { Handle = openArg.Handle };
                    D3DKMTCloseAdapter(ref closeArg);
                }
            }
            catch
            {
                return false;
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
            if (NativeLibrary.TryLoad("nvml.dll", typeof(WindowsGpuTelemetrySensor).Assembly, DllImportSearchPath.System32, out handle))
            {
                return true;
            }

            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrWhiteSpace(programFiles))
            {
                string fallbackPath = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
                if (File.Exists(fallbackPath) && NativeLibrary.TryLoad(fallbackPath, out handle))
                {
                    return true;
                }
            }

            handle = 0;
            return false;
        }

        private void ReleaseLibrary()
        {
            if (_library != 0)
            {
                NativeLibrary.Free(_library);
                _library = 0;
            }

            _nvmlShutdown = null;
            _getPowerUsage = null;
            _getTemperature = null;
            _getClockEventReasons = null;
        }

        public void Dispose()
        {
            if (_initialized && _nvmlShutdown != null)
            {
                try { _nvmlShutdown(); } catch { }
            }

            _initialized = false;
            _adapters.Clear();
            _deviceHandleCache.Clear();
            ReleaseLibrary();
        }
    }

    private sealed unsafe class D3dKmtAdapterSampler
    {
        private const int QueryAdapter = 0;
        private const int QueryNode = 5;
        private const int QueryProcessNode = 6;
        private const int QueryResultOffset = 24;
        private const int QueryInputOffset = 800; // 0x320 on 64-bit Windows (x64 & ARM64)
        private const int KmtQaiTypeNodeMetadata = 25;
        private const double MinSampleIntervalSeconds = 0.08;

        // Matches D3DKMT_NODEMETADATA / DXGK_NODEMETADATA (78 bytes under #pragma pack(1)) from WDK d3dkmdt.h.
        // Using fixed ushort keeps the struct 100% blittable (sizeof == Marshal.SizeOf == 78) on x64 and ARM64.
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct D3dKmtNodeMetadata
        {
            public uint NodeOrdinalAndAdapterIndex;
            public int EngineType;
            public fixed ushort FriendlyName[32];
            public uint Flags;
            public byte GpuMmuSupported;
            public byte IoMmuSupported;
        }

        private readonly uint _luidLow;
        private readonly int _luidHigh;
        private readonly nint _selfProcess;
        private readonly int _nodeCount;
        private readonly bool[] _includeNode;
        private readonly ulong[] _buffer = new ulong[128]; // 1,024 bytes, 8-byte aligned
        private readonly long[] _prevNodeTime;
        private readonly long[] _prevProcNodeTime;
        private readonly long[] _curNodeTime;
        private readonly long[] _curProcNodeTime;
        private long _prevStamp;
        private bool _hasPrev;
        private bool _hasCached;
        private double _cachedTotal;
        private double _cachedSelf;
        private double _cachedOther;

        public bool IsAvailable { get; }

        public D3dKmtAdapterSampler(long adapterLuid)
        {
            _luidLow = (uint)(adapterLuid & 0xFFFFFFFFL);
            _luidHigh = (int)(adapterLuid >> 32);
            _selfProcess = GetCurrentProcess();

            try
            {
                Array.Clear(_buffer, 0, _buffer.Length);
                fixed (ulong* raw = _buffer)
                {
                    byte* p = (byte*)raw;
                    *(int*)(p + 0) = QueryAdapter;
                    *(uint*)(p + 4) = _luidLow;
                    *(int*)(p + 8) = _luidHigh;
                    if (D3DKMTQueryStatistics((nint)p) == 0)
                    {
                        uint nodeCount = *(uint*)(p + QueryResultOffset + 4);
                        if (nodeCount > 0 && nodeCount <= 128)
                        {
                            _nodeCount = (int)nodeCount;
                            IsAvailable = true;
                        }
                    }
                }
            }
            catch
            {
                IsAvailable = false;
            }

            _includeNode = BuildComputeNodeMask(_luidLow, _luidHigh, _nodeCount);
            _prevNodeTime = new long[_nodeCount];
            _prevProcNodeTime = new long[_nodeCount];
            _curNodeTime = new long[_nodeCount];
            _curProcNodeTime = new long[_nodeCount];
        }

        private static bool[] BuildComputeNodeMask(uint luidLow, int luidHigh, int nodeCount)
        {
            var mask = new bool[nodeCount];
            if (nodeCount == 0)
            {
                return mask;
            }

            bool anyMetadataOk = false;
            bool anyIncluded = false;
            try
            {
                var openArg = new D3dKmtOpenAdapterFromLuid
                {
                    AdapterLuid = new Win32Luid { LowPart = luidLow, HighPart = luidHigh }
                };
                if (D3DKMTOpenAdapterFromLuid(ref openArg) == 0 && openArg.Handle != 0)
                {
                    try
                    {
                        uint metaSize = (uint)sizeof(D3dKmtNodeMetadata);
                        for (int node = 0; node < nodeCount; node++)
                        {
                            D3dKmtNodeMetadata meta = default;
                            meta.NodeOrdinalAndAdapterIndex = (uint)node;
                            var queryArg = new D3dKmtQueryAdapterInfo
                            {
                                Handle = openArg.Handle,
                                Type = KmtQaiTypeNodeMetadata,
                                PrivateDriverData = (nint)(&meta),
                                PrivateDriverDataSize = metaSize
                            };

                            if (D3DKMTQueryAdapterInfo(ref queryArg) == 0)
                            {
                                anyMetadataOk = true;
                                string friendly = ReadNullTerminatedName(meta.FriendlyName, 32);
                                bool include = IsComputeContendingEngine(meta.EngineType, friendly);
                                mask[node] = include;
                                if (include)
                                {
                                    anyIncluded = true;
                                }
                            }
                            else
                            {
                                mask[node] = true;
                            }
                        }
                    }
                    finally
                    {
                        var closeArg = new D3dKmtCloseAdapter { Handle = openArg.Handle };
                        D3DKMTCloseAdapter(ref closeArg);
                    }
                }
            }
            catch
            {
                anyMetadataOk = false;
            }

            if (!anyMetadataOk || !anyIncluded)
            {
                Array.Fill(mask, true);
            }

            return mask;
        }

        private static string ReadNullTerminatedName(ushort* chars, int maxLength)
        {
            int len = 0;
            while (len < maxLength && chars[len] != 0)
            {
                len++;
            }

            return len > 0 ? new string((char*)chars, 0, len) : string.Empty;
        }

        public bool TrySample(out double totalUtil, out double selfUtil, out double otherUtil)
        {
            totalUtil = 0.0;
            selfUtil = 0.0;
            otherUtil = 0.0;
            if (!IsAvailable)
            {
                return false;
            }

            long nowStamp = Stopwatch.GetTimestamp();
            if (_hasPrev)
            {
                double dtSec = Stopwatch.GetElapsedTime(_prevStamp, nowStamp).TotalSeconds;
                if (dtSec < MinSampleIntervalSeconds)
                {
                    if (_hasCached)
                    {
                        totalUtil = _cachedTotal;
                        selfUtil = _cachedSelf;
                        otherUtil = _cachedOther;
                        return true;
                    }

                    return false;
                }
            }

            bool anyNodeQueryOk = false;
            try
            {
                fixed (ulong* raw = _buffer)
                {
                    byte* p = (byte*)raw;
                    for (int node = 0; node < _nodeCount; node++)
                    {
                        if (!_includeNode[node])
                        {
                            continue;
                        }

                        Array.Clear(_buffer);
                        *(int*)(p + 0) = QueryNode;
                        *(uint*)(p + 4) = _luidLow;
                        *(int*)(p + 8) = _luidHigh;
                        *(uint*)(p + QueryInputOffset) = (uint)node;
                        if (D3DKMTQueryStatistics((nint)p) == 0)
                        {
                            _curNodeTime[node] = *(long*)(p + QueryResultOffset);
                            anyNodeQueryOk = true;
                        }
                        else
                        {
                            _curNodeTime[node] = _prevNodeTime[node];
                        }

                        Array.Clear(_buffer);
                        *(int*)(p + 0) = QueryProcessNode;
                        *(uint*)(p + 4) = _luidLow;
                        *(int*)(p + 8) = _luidHigh;
                        *(nint*)(p + 16) = _selfProcess;
                        *(uint*)(p + QueryInputOffset) = (uint)node;
                        _curProcNodeTime[node] = D3DKMTQueryStatistics((nint)p) == 0
                            ? *(long*)(p + QueryResultOffset)
                            : _prevProcNodeTime[node];
                    }
                }
            }
            catch
            {
                return false;
            }

            if (!anyNodeQueryOk)
            {
                return false;
            }

            if (!_hasPrev)
            {
                Array.Copy(_curNodeTime, _prevNodeTime, _nodeCount);
                Array.Copy(_curProcNodeTime, _prevProcNodeTime, _nodeCount);
                _prevStamp = nowStamp;
                _hasPrev = true;
                return false;
            }

            double elapsed100ns = Stopwatch.GetElapsedTime(_prevStamp, nowStamp).TotalSeconds * 10_000_000.0;
            if (elapsed100ns <= 0.0)
            {
                return false;
            }

            double maxTotal = 0.0;
            double maxSelf = 0.0;
            double maxOther = 0.0;
            for (int n = 0; n < _nodeCount; n++)
            {
                if (!_includeNode[n])
                {
                    continue;
                }

                long dNode = Math.Max(0L, _curNodeTime[n] - _prevNodeTime[n]);
                long dProc = Math.Max(0L, _curProcNodeTime[n] - _prevProcNodeTime[n]);
                double tPct = Math.Clamp(100.0 * dNode / elapsed100ns, 0.0, 100.0);
                double sPct = Math.Clamp(100.0 * dProc / elapsed100ns, 0.0, 100.0);
                double oPct = Math.Clamp(100.0 * Math.Max(0L, dNode - dProc) / elapsed100ns, 0.0, 100.0);
                if (tPct > maxTotal) maxTotal = tPct;
                if (sPct > maxSelf) maxSelf = sPct;
                if (oPct > maxOther) maxOther = oPct;
            }

            Array.Copy(_curNodeTime, _prevNodeTime, _nodeCount);
            Array.Copy(_curProcNodeTime, _prevProcNodeTime, _nodeCount);
            _prevStamp = nowStamp;

            _cachedTotal = maxTotal;
            _cachedSelf = maxSelf;
            _cachedOther = maxOther;
            _hasCached = true;

            totalUtil = maxTotal;
            selfUtil = maxSelf;
            otherUtil = maxOther;
            return true;
        }

        [DllImport("gdi32.dll")]
        private static extern int D3DKMTQueryStatistics(nint queryStatisticsPtr);

        [DllImport("kernel32.dll")]
        private static extern nint GetCurrentProcess();
    }
}
