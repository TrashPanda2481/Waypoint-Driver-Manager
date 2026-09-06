// The install plan built from a scan. Ported from engine/session.py.

using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Waypoint.Core;

namespace Waypoint.Engine;

// Status is the wire string ("upgrade_available", ...) so the plan JSON matches Python's.
public sealed record PlanEntry(
    string InstanceId,
    string FriendlyName,
    string Status,
    DriverCandidate? ChosenCandidate,
    bool Ambiguous,
    bool RequiresConfirmation);

public sealed class Plan
{
    // Two-space indent and literal '&' match json.dumps(..., indent=2) in the Python CLI.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = true,
    };

    public List<PlanEntry> Entries { get; init; } = [];

    // Stands in for Python's to_dict(): the CLI dumps this straight to stdout/file.
    public string ToJson()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("entries");
            foreach (var entry in Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("instance_id", entry.InstanceId);
                writer.WriteString("friendly_name", entry.FriendlyName);
                writer.WriteString("status", entry.Status);
                writer.WritePropertyName("chosen_candidate");
                if (entry.ChosenCandidate is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, entry.ChosenCandidate, WaypointJsonContext.Default.DriverCandidate);
                }

                writer.WriteBoolean("ambiguous", entry.Ambiguous);
                writer.WriteBoolean("requires_confirmation", entry.RequiresConfirmation);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
