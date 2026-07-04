using System.Text.Json;
using System.Text.Json.Serialization;
using NodaTime;
using NodaTime.Serialization.SystemTextJson;

namespace Klaxon.Tests.Integration.Infrastructure;

internal static class TestJson
{
    // Mirrors Program.cs' ConfigureHttpJsonOptions so request bodies and responses use the same
    // NodaTime + string-enum shape the API speaks. The default HttpClient serializer knows neither
    // Instant/LocalTime nor string enums, so without these options every NodaTime field is
    // written or read wrong and the round-trip fails before it reaches the assertion.
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.ConfigureForNodaTime(DateTimeZoneProviders.Tzdb);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
