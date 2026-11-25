using System.Runtime.InteropServices;

namespace PGPMint.Core.Gpu;

internal static unsafe class OpenClNative
{
    static OpenClNative() => NativeGpuLibraries.Initialize();
    public const int ClSuccess = 0;
    public const int ClDeviceNotAvailable = -2;
    public const ulong ClDeviceTypeGpu = 1UL << 2;

    public const uint ClDeviceVendorId = 0x1001;
    public const uint ClDeviceType = 0x1000;
    public const uint ClDeviceHostUnifiedMemory = 0x1035;
    public const uint ClDeviceUuidKhr = 0x106A;
    public const uint ClDevicePciBusInfoKhr = 0x410F;
    public const uint ClDeviceMaxWorkGroupSize = 0x1004;
    public const uint ClDeviceMaxParameterSize = 0x1017;
    public const uint ClDeviceGlobalMemSize = 0x101F;
    public const uint ClDeviceName = 0x102B;
    public const uint ClDriverVersion = 0x102D;
    public const uint ClDeviceVersion = 0x102F;
    public const uint ClDeviceExtensions = 0x1030;
    public const uint ClDeviceLuidKhr = 0x106D;
    public const uint ClDeviceLuidValidKhr = 0x106C;

    public const uint ClProgramBinarySizes = 0x1165;
    public const uint ClProgramBinaries = 0x1166;
    public const uint ClProgramBuildLog = 0x1183;
    public const uint ClKernelWorkGroupSize = 0x11B0;

    public const ulong ClMemReadWrite = 1UL << 0;
    public const ulong ClMemReadOnly = 1UL << 2;
    public const ulong ClMemAllocHostPtr = 1UL << 4;

    private static readonly Lazy<nint> LibraryHandle = new(() => NativeGpuLibraries.Handle("OpenCL.dll"));

    private static readonly Lazy<bool> HasFillBufferExport = new(() =>
        LibraryHandle.Value != 0 && NativeLibrary.TryGetExport(LibraryHandle.Value, "clEnqueueFillBuffer", out _));

    public static bool IsAvailable() => LibraryHandle.Value != 0;
    public static bool SupportsFillBufferExport() => HasFillBufferExport.Value;

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clGetPlatformIDs(uint numEntries, nint* platforms, uint* numPlatforms);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clGetDeviceIDs(nint platform, ulong deviceType, uint numEntries, nint* devices, uint* numDevices);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clGetDeviceInfo(nint device, uint paramName, nuint paramValueSize, void* paramValue, nuint* paramValueSizeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern nint clCreateContext(nint* properties, uint numDevices, nint* devices, nint pfnNotify, nint userData, int* errcodeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clReleaseContext(nint context);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern nint clCreateCommandQueue(nint context, nint device, ulong properties, int* errcodeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clReleaseCommandQueue(nint commandQueue);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern nint clCreateProgramWithSource(nint context, uint count, byte** strings, nuint* lengths, int* errcodeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern nint clCreateProgramWithBinary(nint context, uint numDevices, nint* devices, nuint* lengths,
        byte** binaries, int* binaryStatus, int* errcodeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clGetProgramInfo(nint program, uint paramName, nuint paramValueSize, void* paramValue,
        nuint* paramValueSizeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clBuildProgram(nint program, uint numDevices, nint* deviceList, byte* options, nint pfnNotify, nint userData);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clGetProgramBuildInfo(nint program, nint device, uint paramName, nuint paramValueSize, void* paramValue, nuint* paramValueSizeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clReleaseProgram(nint program);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern nint clCreateKernel(nint program, byte* kernelName, int* errcodeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clGetKernelWorkGroupInfo(nint kernel, nint device, uint paramName, nuint paramValueSize, void* paramValue, nuint* paramValueSizeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clReleaseKernel(nint kernel);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern nint clCreateBuffer(nint context, ulong flags, nuint size, void* hostPtr, int* errcodeRet);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clReleaseMemObject(nint memObj);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clSetKernelArg(nint kernel, uint argIndex, nuint argSize, void* argValue);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clEnqueueWriteBuffer(
        nint commandQueue, nint buffer, uint blockingWrite, nuint offset, nuint size, void* ptr,
        uint numEventsInWaitList, nint* eventWaitList, nint* eventOut);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clEnqueueFillBuffer(
        nint commandQueue, nint buffer, void* pattern, nuint patternSize, nuint offset, nuint size,
        uint numEventsInWaitList, nint* eventWaitList, nint* eventOut);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clEnqueueNDRangeKernel(
        nint commandQueue, nint kernel, uint workDim, nuint* globalWorkOffset, nuint* globalWorkSize, nuint* localWorkSize,
        uint numEventsInWaitList, nint* eventWaitList, nint* eventOut);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clEnqueueReadBuffer(
        nint commandQueue, nint buffer, uint blockingRead, nuint offset, nuint size, void* ptr,
        uint numEventsInWaitList, nint* eventWaitList, nint* eventOut);

    [DllImport("OpenCL.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern int clFinish(nint commandQueue);
}
