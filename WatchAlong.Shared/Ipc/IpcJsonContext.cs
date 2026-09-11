using System.Text.Json.Serialization;

namespace WatchAlong.Shared.Ipc;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IpcMessage))]
internal partial class IpcJsonContext : JsonSerializerContext;
