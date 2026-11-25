using System.Text.Json.Serialization;
using PGPMint.Core.Search;

namespace PGPMint.Core.Storage;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppPersistedState))]
[JsonSerializable(typeof(CalibrationRecord))]
[JsonSerializable(typeof(EfficiencyProfile))]
[JsonSerializable(typeof(TierOperatingPoint))]
[JsonSerializable(typeof(EfficiencyCurveSample))]
[JsonSerializable(typeof(PowerTier))]
[JsonSerializable(typeof(List<MatchedKeyRecord>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
}
