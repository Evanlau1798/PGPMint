using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PGPMint.Core.Search;
using PGPMint.Core.Storage;

namespace PGPMint.Core.Gpu;

internal static unsafe class OpenClProgramCache
{
    private const int MaxBinaryBytes = 16 * 1024 * 1024;
    private const int DigestHexCharacters = 64;
    private const int MaxCacheCharacters = DigestHexCharacters + 1 + 4 * ((MaxBinaryBytes + 2) / 3);

    internal static (nint Program, nint Kernel, bool Cached) Create(
        nint context, nint device, GpuDeviceInfo info, string cacheDirectory, bool useByValueSchedule = true)
    {
        byte[] source = Encoding.ASCII.GetBytes(OpenClKernelSource.BuildFourKeyOpenCl(useByValueSchedule));
        string path = CachePath(cacheDirectory, info, source);
        byte[]? binary = ReadBinary(path);
        if (binary is not null && TryOpenBinary(context, device, binary, out nint cachedProgram, out nint cachedKernel))
        {
            return (cachedProgram, cachedKernel, true);
        }

        int error = 0;
        nint program;
        nuint sourceLength = (nuint)source.Length;
        fixed (byte* sourcePointer = source)
        {
            byte* sourceArgument = sourcePointer;
            program = OpenClNative.clCreateProgramWithSource(context, 1, &sourceArgument, &sourceLength, &error);
        }

        nint kernel = 0;
        try
        {
            if (error != OpenClNative.ClSuccess || program == 0)
            {
                throw new InvalidOperationException($"clCreateProgramWithSource failed ({error}).");
            }

            error = OpenClNative.clBuildProgram(program, 1, &device, null, 0, 0);
            if (error != OpenClNative.ClSuccess)
            {
                throw new InvalidOperationException($"OpenCL kernel build failed ({error}): {ReadBuildLog(program, device)}");
            }

            kernel = CreateSearchKernel(program, &error);
            if (error != OpenClNative.ClSuccess || kernel == 0)
            {
                throw new InvalidOperationException($"clCreateKernel failed ({error}).");
            }

            TryWriteBinary(path, program);
            return (program, kernel, false);
        }
        catch
        {
            if (kernel != 0) OpenClNative.clReleaseKernel(kernel);
            if (program != 0) OpenClNative.clReleaseProgram(program);
            throw;
        }
    }

    private static string CachePath(string directory, GpuDeviceInfo info, byte[] source)
    {
        var identity = FormattableString.Invariant(
            $"v1|{RuntimeInformation.ProcessArchitecture}|{info.DeviceKey}|{info.Name}|{info.DriverVersion}|{info.BackendDriverVersion}|{info.KernelVersion}|default-build-options|{Convert.ToHexString(SHA256.HashData(source))}");
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(directory, $"{key}.clbin");
    }

    private static byte[]? ReadBinary(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            long length = stream.Length;
            if (length <= DigestHexCharacters + 1 || length > MaxCacheCharacters)
            {
                return null;
            }

            var encoded = new byte[(int)length];
            stream.ReadExactly(encoded);
            if (encoded[DigestHexCharacters] != (byte)'\n')
            {
                return null;
            }

            string storedDigest = Encoding.ASCII.GetString(encoded, 0, DigestHexCharacters);
            byte[] binary = Convert.FromBase64String(Encoding.ASCII.GetString(encoded, DigestHexCharacters + 1,
                encoded.Length - DigestHexCharacters - 1));
            return binary.Length is > 0 and <= MaxBinaryBytes &&
                string.Equals(storedDigest, Convert.ToHexString(SHA256.HashData(binary)), StringComparison.Ordinal)
                ? binary : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static bool TryOpenBinary(nint context, nint device, byte[] binary, out nint program, out nint kernel)
    {
        program = 0;
        kernel = 0;
        int error = 0;
        int binaryStatus = 0;
        nuint length = (nuint)binary.Length;
        bool accepted = false;
        try
        {
            fixed (byte* binaryPointer = binary)
            {
                byte* argument = binaryPointer;
                program = OpenClNative.clCreateProgramWithBinary(context, 1, &device, &length, &argument, &binaryStatus, &error);
            }

            if (error != OpenClNative.ClSuccess || binaryStatus != OpenClNative.ClSuccess || program == 0 ||
                OpenClNative.clBuildProgram(program, 1, &device, null, 0, 0) != OpenClNative.ClSuccess)
            {
                return false;
            }

            kernel = CreateSearchKernel(program, &error);
            accepted = error == OpenClNative.ClSuccess && kernel != 0;
            return accepted;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (!accepted)
            {
                if (kernel != 0) OpenClNative.clReleaseKernel(kernel);
                if (program != 0) OpenClNative.clReleaseProgram(program);
            }
        }
    }

    private static nint CreateSearchKernel(nint program, int* error)
    {
        fixed (byte* name = "search\0"u8)
        {
            return OpenClNative.clCreateKernel(program, name, error);
        }
    }

    private static void TryWriteBinary(string path, nint program)
    {
        try
        {
            nuint length = 0;
            if (OpenClNative.clGetProgramInfo(program, OpenClNative.ClProgramBinarySizes, (nuint)sizeof(nuint),
                &length, null) != OpenClNative.ClSuccess || length == 0 || length > MaxBinaryBytes)
            {
                return;
            }

            var binary = new byte[(int)length];
            fixed (byte* binaryPointer = binary)
            {
                byte* argument = binaryPointer;
                if (OpenClNative.clGetProgramInfo(program, OpenClNative.ClProgramBinaries, (nuint)sizeof(nint),
                    &argument, null) != OpenClNative.ClSuccess)
                {
                    return;
                }
            }

            string digest = Convert.ToHexString(SHA256.HashData(binary));
            AtomicFileStore.WriteTextAtomic(path, $"{digest}\n{Convert.ToBase64String(binary)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EntryPointNotFoundException)
        {
            // The source-built kernel is usable even if this driver or directory cannot cache it.
        }
    }

    private static string ReadBuildLog(nint program, nint device)
    {
        nuint logSize = 0;
        if (OpenClNative.clGetProgramBuildInfo(program, device, OpenClNative.ClProgramBuildLog, 0, null, &logSize) != OpenClNative.ClSuccess ||
            logSize is 0 or > 65536)
        {
            return "(no build log)";
        }

        byte[] buffer = new byte[(int)logSize];
        fixed (byte* pointer = buffer)
        {
            OpenClNative.clGetProgramBuildInfo(program, device, OpenClNative.ClProgramBuildLog, logSize, pointer, null);
        }

        int length = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, length >= 0 ? length : buffer.Length).Trim();
    }
}
