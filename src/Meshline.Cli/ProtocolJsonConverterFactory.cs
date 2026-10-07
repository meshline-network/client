using Meshline.Models.Protocol;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Cli;

// Keep signed documents in the SDK's protocol representation, including byte encoding.
internal sealed class ProtocolJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeof(ProtocolModel).IsAssignableFrom(typeToConvert);
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(ProtocolJsonConverter<>).MakeGenericType(typeToConvert))!;
    sealed class ProtocolJsonConverter<T> : JsonConverter<T> where T : ProtocolModel
    {
        public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            using var document = JsonDocument.ParseValue(ref reader);
            return ProtocolModel.FromJson<T>(document.RootElement.GetRawText());
        }
        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToJson());
    }
}
