using System.Buffers;
using System.Text.Json;

namespace WebhookService.Infrastructure.Dispatch.RabbitMq;

/// <summary>
/// The message that announces a delivery (ADR 0010): the id of the delivery and nothing else. The payload, the address
/// and the signing secret stay in the database, so the broker never holds them and a lost or duplicated message does
/// no harm: it only says "try this delivery".
///
/// Anything that arrives on the queue is untrusted until it parses, so parsing never throws and never reads more than
/// a short message can need.
/// </summary>
public static class DeliveryMessage
{
    private const string IdProperty = "deliveryId";

    // A real message is about 55 bytes. Anything much larger is not one, and is refused unread.
    private const int MaxBodyLength = 1024;

    private static readonly JsonReaderOptions ReaderOptions = new() { MaxDepth = 8 };

    public static byte[] Create(Guid deliveryId)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(IdProperty, deliveryId);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads the delivery id. Properties it does not know are ignored, so a newer sender can add some.</summary>
    public static bool TryParse(ReadOnlySpan<byte> body, out Guid deliveryId)
    {
        deliveryId = Guid.Empty;

        if (body.IsEmpty || body.Length > MaxBodyLength)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(body, ReaderOptions);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            Guid? found = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var isId = reader.ValueTextEquals(IdProperty);
                reader.Read();

                if (isId)
                {
                    if (reader.TokenType != JsonTokenType.String || !reader.TryGetGuid(out var guid) || guid == Guid.Empty)
                    {
                        return false;
                    }

                    found = guid;
                }
                else
                {
                    reader.Skip();
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || found is null)
            {
                return false;
            }

            deliveryId = found.Value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
