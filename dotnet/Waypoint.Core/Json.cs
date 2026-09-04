// Shared JSON policy. Keeps the on-disk format byte-compatible with the
// Python implementation's manifest/audit records (snake_case keys, ISO dates).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Waypoint.Core;

// Fails closed on an unrecognized tier, mirroring Python's SignatureType(...)
// raising ValueError. Never guess a trust tier from bad input.
public sealed class SignatureTypeJsonConverter : JsonConverter<SignatureType>
{
    public override SignatureType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return raw switch
        {
            "whql" => SignatureType.Whql,
            "attestation" => SignatureType.Attestation,
            "test_signed" => SignatureType.TestSigned,
            "unsigned" => SignatureType.Unsigned,
            _ => throw new JsonException(
                $"Unrecognized signature_type '{raw}'. Refusing to guess a driver trust tier."),
        };
    }

    public override void Write(Utf8JsonWriter writer, SignatureType value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToWireString());
}

// Source-generated so the AOT-published CLI has no reflection dependency.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(List<DriverCandidate>))]
[JsonSerializable(typeof(DriverCandidate))]
public partial class WaypointJsonContext : JsonSerializerContext;
