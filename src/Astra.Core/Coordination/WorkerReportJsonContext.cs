using System.Text.Json.Serialization;

namespace Astra.Core.Coordination;

[JsonSerializable(typeof(WorkerReport))]
internal sealed partial class WorkerReportJsonContext : JsonSerializerContext;
