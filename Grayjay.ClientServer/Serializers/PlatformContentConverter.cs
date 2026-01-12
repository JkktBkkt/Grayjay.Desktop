using Grayjay.Engine.Models;
using Grayjay.Engine.Models.Feed;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grayjay.ClientServer.Serializers
{
    public class PlatformContentConverter : JsonConverter<PlatformContent>
    {
        public override bool CanConvert(Type typeToConvert)
        {
            return base.CanConvert(typeToConvert);
        }

        public override PlatformContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            Utf8JsonReader readerClone = reader;

            using var doc = JsonDocument.ParseValue(ref readerClone);

            if (!doc.RootElement.TryGetProperty(nameof(PlatformContent.ContentType), out var ctProp))
                throw new JsonException($"Missing '{nameof(PlatformContent.ContentType)}' discriminator.");

            if (ctProp.ValueKind != JsonValueKind.Number)
                throw new JsonException($"'{nameof(PlatformContent.ContentType)}' must be a number.");

            var typeDiscriminator = (ContentType)ctProp.GetInt32();
            return typeDiscriminator switch
            {
                ContentType.MEDIA => JsonSerializer.Deserialize<PlatformVideo>(ref reader)!,
                _ => JsonSerializer.Deserialize<PlatformContent>(ref reader)! //TODO: Recursion intended??
            };
        }

        public override void Write(Utf8JsonWriter writer, PlatformContent value, JsonSerializerOptions options)
        {
            JsonSerializer.Serialize(writer, value);
        }
    }
}
