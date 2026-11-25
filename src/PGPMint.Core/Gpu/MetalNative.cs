using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

internal static unsafe class MetalNative
{
    private const string Library = "pgpmint_metal";
    internal const int ErrorCapacity = 4096;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Adapter
    {
        public ulong RegistryId;
        public ulong WorkingSetBytes;
        public uint UnifiedMemory;
        public uint LowPower;
        private fixed byte _name[256];

        internal string GetName()
        {
            fixed (byte* text = _name)
            {
                int length = 0;
                while (length < 256 && text[length] != 0) length++;
                return Encoding.UTF8.GetString(text, length);
            }
        }
    }

    internal static Adapter[] Enumerate()
    {
        Adapter[] adapters = [];
        byte[] error = new byte[ErrorCapacity];
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint count;
            fixed (Adapter* buffer = adapters)
                Check(EnumerateDevices(buffer, (uint)adapters.Length, out count, error, (uint)error.Length), error);
            if (count <= adapters.Length) return adapters.AsSpan(0, (int)count).ToArray();
            if (count > 1024) throw new InvalidOperationException("Metal returned an invalid device count.");
            adapters = new Adapter[(int)count];
        }
        throw new InvalidOperationException("Metal device list changed repeatedly during enumeration.");
    }

    internal static void Check(int status, byte[] error)
    {
        if (status == 0) return;
        int length = Array.IndexOf(error, (byte)0);
        string message = Encoding.UTF8.GetString(error, 0, length < 0 ? error.Length : length);
        if (status == -2) throw new GpuDeviceLostException($"Metal command failed: {message}");
        throw new InvalidOperationException($"Metal operation failed ({status}): {message}");
    }

    [DllImport(Library, EntryPoint = "pgpmint_metal_enumerate", CallingConvention = CallingConvention.Cdecl)]
    private static extern int EnumerateDevices(Adapter* devices, uint capacity, out uint count,
        [Out] byte[] error, uint errorCapacity);

    [DllImport(Library, EntryPoint = "pgpmint_metal_create", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Create(ulong registryId, uint requestedWorkgroupSize,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        out nint session, out uint workgroupSize, [Out] byte[] error, uint errorCapacity);

    [DllImport(Library, EntryPoint = "pgpmint_metal_dispatch", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Dispatch(MetalHandle session, uint* schedule, uint scheduleWords,
        in MetalDispatchParameters parameters, uint* results, ulong resultWords, [Out] byte[] error, uint errorCapacity);

    [DllImport(Library, EntryPoint = "pgpmint_metal_destroy", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void Destroy(nint session);
}

internal sealed class MetalHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal MetalHandle(nint value) : base(true) => SetHandle(value);

    protected override bool ReleaseHandle()
    {
        MetalNative.Destroy(handle);
        return true;
    }
}

internal sealed unsafe class MetalSession : IMetalSession
{
    private readonly MetalHandle _handle;
    private readonly byte[] _error = new byte[MetalNative.ErrorCapacity];
    public int WorkgroupSize { get; }

    private MetalSession(MetalHandle handle, int workgroupSize)
    {
        _handle = handle;
        WorkgroupSize = workgroupSize;
    }

    internal static MetalSession Create(ulong registryId, int requestedWorkgroupSize)
    {
        byte[] error = new byte[MetalNative.ErrorCapacity];
        MetalNative.Check(MetalNative.Create(registryId, (uint)requestedWorkgroupSize, MetalKernelSource.BuildFourKeyMetal(),
            out nint session, out uint workgroupSize, error, (uint)error.Length), error);
        var handle = new MetalHandle(session);
        if (handle.IsInvalid || workgroupSize < 1 || workgroupSize > requestedWorkgroupSize)
        {
            handle.Dispose();
            throw new InvalidOperationException("Metal returned an invalid session configuration.");
        }
        return new MetalSession(handle, (int)workgroupSize);
    }

    public void Dispatch(uint[] schedule, MetalDispatchParameters parameters, uint[] results)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        fixed (uint* words = schedule)
        fixed (uint* output = results)
            MetalNative.Check(MetalNative.Dispatch(_handle, words, (uint)schedule.Length, in parameters,
                output, (ulong)results.LongLength, _error, (uint)_error.Length), _error);
    }

    public void Dispose() => _handle.Dispose();
}
