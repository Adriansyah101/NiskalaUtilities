using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Niskala.Cli.Commands;
using Niskala.Tracker.Logs;

namespace Niskala.Cli;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ListOutput))]
[JsonSerializable(typeof(SizeOutput))]
[JsonSerializable(typeof(IconsOutput))]
[JsonSerializable(typeof(InspectOutput))]
[JsonSerializable(typeof(UninstallOutput))]
[JsonSerializable(typeof(SteamOutput))]
[JsonSerializable(typeof(BatchOutput))]
[JsonSerializable(typeof(LeftoversOutput))]
[JsonSerializable(typeof(CleanOutput))]
[JsonSerializable(typeof(RestoreListOutput))]
[JsonSerializable(typeof(RestoreOutput))]
[JsonSerializable(typeof(ForceOutput))]
[JsonSerializable(typeof(TrackOutput))]
[JsonSerializable(typeof(LogsListOutput))]
[JsonSerializable(typeof(InstallLog))]
[JsonSerializable(typeof(RollbackOutput))]
[JsonSerializable(typeof(RepairListOutput))]
[JsonSerializable(typeof(RepairRunOutput))]
[JsonSerializable(typeof(CleanRulesOutput))]
[JsonSerializable(typeof(CleanRunOutput))]
[JsonSerializable(typeof(IntegrityOutput))]
[JsonSerializable(typeof(ResourceListOutput))]
[JsonSerializable(typeof(ResourceChangeOutput))]
[JsonSerializable(typeof(ProcessListOutput))]
[JsonSerializable(typeof(RestraintOutput))]
[JsonSerializable(typeof(StorageMapOutput))]
[JsonSerializable(typeof(DuplicateOutput))]
[JsonSerializable(typeof(WipeOutput))]
internal sealed partial class CliJsonContext : JsonSerializerContext;

internal static class CliJson
{
    public static CliJsonContext Output { get; } = new(new JsonSerializerOptions(CliJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
