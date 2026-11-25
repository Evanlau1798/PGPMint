using System.Text.Json.Serialization;
using PGPMint.Core.Search;

namespace PGPMint.Core.Gpu;

internal sealed record GpuCandidateIdentity(string DeviceKey, string BackendName, string DriverVersion,
    string? BackendDriverVersion, string KernelVersion, GpuKernelConfiguration? KernelConfiguration);
internal sealed record GpuSelectionCandidate(string Identity, string Workload);
internal sealed record GpuSelectionIdentity(string DeviceKey, GpuSelectionCandidate[] Candidates);

[JsonSerializable(typeof(GpuCandidateIdentity))]
[JsonSerializable(typeof(GpuSelectionIdentity))]
internal sealed partial class GpuSelectionJsonContext : JsonSerializerContext { }
