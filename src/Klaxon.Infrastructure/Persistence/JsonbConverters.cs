using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Klaxon.Infrastructure.Persistence;

// Serializes an encapsulated list into a jsonb column. Used for Schedule.ParticipantOrder and
// EscalationLevel.Targets, which are deliberately denormalized to jsonb rather than child tables
// (see ADR-004). The comparer gives EF a correct snapshot/equality for the mutable collection so
// change tracking does not silently miss edits or false-positive on every save.
internal static class JsonbConverters
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ValueConverter<IReadOnlyList<T>, string> Converter<T>() =>
        new(
            list => JsonSerializer.Serialize(list, Options),
            json => JsonSerializer.Deserialize<List<T>>(json, Options) ?? new List<T>());

    public static ValueComparer<IReadOnlyList<T>> Comparer<T>() =>
        new(
            (a, b) => a!.SequenceEqual(b!),
            list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item!.GetHashCode())),
            list => (IReadOnlyList<T>)list.ToList());
}
