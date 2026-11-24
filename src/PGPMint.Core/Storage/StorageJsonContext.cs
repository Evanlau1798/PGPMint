using System.Text.Json.Serialization;
using PGPMint.Core.Search;

namespace PGPMint.Core.Storage;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppPersistedState))]
[JsonSerializable(typeof(CalibrationRecord))]
[JsonSerializable(typeof(List<MatchedKeyRecord>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext
{
}
