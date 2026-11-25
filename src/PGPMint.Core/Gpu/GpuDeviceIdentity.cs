using PGPMint.Core.Search;
using System.Buffers.Binary;

namespace PGPMint.Core.Gpu;

internal static class GpuDeviceIdentity
{
    internal static int ValidateWorkgroup(int workgroupSize) => workgroupSize is 64 or 128 or 256
        ? workgroupSize : throw new ArgumentOutOfRangeException(nameof(workgroupSize), "Workgroup must be 64, 128 or 256.");

    internal static ulong? MoltenVkRegistryId(ReadOnlySpan<byte> luid, ReadOnlySpan<byte> driverUuid, bool macOS, bool valid)
    {
        // MoltenVK writes registryID to deviceLUID in big-endian order; UUID uses a different location ID.
        if (!macOS || !valid || luid.Length != 8 || driverUuid.Length != 16 || !driverUuid[..4].SequenceEqual("MVK\0"u8)) return null;
        ulong registryId = BinaryPrimitives.ReadUInt64BigEndian(luid);
        return registryId == 0 ? null : registryId;
    }

    internal static string? NormalizeUuid(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 16 && bytes.ContainsAnyExcept((byte)0) ? Convert.ToHexString(bytes).ToLowerInvariant() : null;

    internal static string Key(string? uuid, string? pci, long? luid) =>
        !string.IsNullOrWhiteSpace(uuid) ? $"uuid:{uuid.ToLowerInvariant()}" :
        !string.IsNullOrWhiteSpace(pci) ? $"pci:{pci.ToLowerInvariant()}" :
        luid.HasValue ? $"luid:{luid.Value:x16}" : string.Empty;

    internal static bool Same(GpuDeviceInfo a, GpuDeviceInfo b) =>
        Equal(a.DeviceUuid, b.DeviceUuid) || Equal(a.PciAddress, b.PciAddress) ||
        (a.MetalRegistryId.HasValue && a.MetalRegistryId == b.MetalRegistryId) ||
        (a.AdapterLuid != 0 && a.AdapterLuid == b.AdapterLuid);

    internal static bool Different(GpuDeviceInfo a, GpuDeviceInfo b) =>
        !Same(a, b) && (Comparable(a.DeviceUuid, b.DeviceUuid) || Comparable(a.PciAddress, b.PciAddress) ||
            (a.MetalRegistryId.HasValue && b.MetalRegistryId.HasValue) ||
            (a.AdapterLuid != 0 && b.AdapterLuid != 0));

    private static bool Comparable(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b);
    private static bool Equal(string? a, string? b) => Comparable(a, b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
