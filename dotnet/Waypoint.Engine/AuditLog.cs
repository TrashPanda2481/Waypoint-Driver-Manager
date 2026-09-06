// Append-only JSON Lines audit trail: what Waypoint did on this machine, and
// when. Ported from engine/audit.py.

using System.Buffers;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Waypoint.Engine;

public sealed class AuditLog
{
    // Python's datetime.now(timezone.utc).isoformat().
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.ffffff'+00:00'";

    // Relaxed escaping so '&' inside a hardware ID stays literal, as json.dumps leaves it.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    public AuditLog(string logPath)
    {
        LogPath = logPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(logPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public string LogPath { get; }

    // Written by hand: reflection serialization would not survive AOT trimming.
    public void Record(string eventName, params (string Key, object? Value)[] fields)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("timestamp", DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture));
            writer.WriteString("event", eventName);
            foreach (var (key, value) in fields)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        stream.Write(buffer.WrittenSpan);
        stream.WriteByte((byte)'\n');
    }

    public List<Dictionary<string, JsonElement>> ReadAll()
    {
        if (!File.Exists(LogPath))
        {
            return [];
        }

        var records = new List<Dictionary<string, JsonElement>>();
        foreach (var line in File.ReadLines(LogPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            var record = new Dictionary<string, JsonElement>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                // Cloned: the element dies with the document.
                record[property.Name] = property.Value.Clone();
            }

            records.Add(record);
        }

        return records;
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case DateOnly date:
                writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case IEnumerable<string> items:
                writer.WriteStartArray();
                foreach (var item in items)
                {
                    writer.WriteStringValue(item);
                }

                writer.WriteEndArray();
                break;
            // Python's json.dumps(default=str) fallback.
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
