using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Spritely.Managers.DataStore
{
    /// <summary>
    /// Configuration for data store instances.
    /// </summary>
    public class DataStoreOptions
    {
        /// <summary>Current schema version. Stored in the envelope so migrations can be applied on load.</summary>
        public int SchemaVersion { get; init; } = 1;

        /// <summary>When true, writes use a .tmp file + atomic move to prevent corruption.</summary>
        public bool AtomicWrite { get; init; } = true;

        /// <summary>When true, uses SafeFileWriter for fire-and-forget background persistence.</summary>
        public bool BackgroundWrite { get; init; }

        /// <summary>JSON serializer options. Defaults to indented with enum-as-string.</summary>
        public JsonSerializerOptions JsonOptions { get; init; } = DefaultJsonOptions;

        /// <summary>
        /// Optional migration function. Called when loaded version &lt; SchemaVersion.
        /// Receives (rawJson, fromVersion) and returns migrated JSON string.
        /// </summary>
        public Func<string, int, string>? Migrator { get; init; }

        /// <summary>Caller name for logging and SafeFileWriter attribution.</summary>
        public string CallerName { get; init; } = "DataStore";

        public static readonly JsonSerializerOptions DefaultJsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public static readonly JsonSerializerOptions CompactJsonOptions = new()
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };
    }

    /// <summary>
    /// Thin envelope wrapping stored data with a schema version for migration support.
    /// </summary>
    public class VersionedEnvelope<T>
    {
        [JsonPropertyName("v")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("data")]
        public T? Data { get; set; }
    }
}
