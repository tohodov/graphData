using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GraphData.Core.Models;

[JsonConverter(typeof(NodeLocalIdJsonConverter))]
public readonly struct NodeLocalId : IEquatable<NodeLocalId>, IComparable<NodeLocalId>
{
    private readonly string? _value;

    public NodeLocalId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    public string Value => _value ?? string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    public ReadOnlySpan<char> AsSpan() => Value.AsSpan();

    public bool Equals(NodeLocalId other) =>
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public int CompareTo(NodeLocalId other) =>
        string.Compare(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) =>
        obj is NodeLocalId other && Equals(other);

    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static NodeLocalId From(string value) => new(value);

    public static bool operator ==(NodeLocalId left, NodeLocalId right) => left.Equals(right);

    public static bool operator !=(NodeLocalId left, NodeLocalId right) => !left.Equals(right);

    public static NodeLocalIdComparer OrdinalIgnoreCaseComparer { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class NodeLocalIdComparer : IEqualityComparer<NodeLocalId>, IComparer<NodeLocalId>
{
    private readonly StringComparer _comparer;

    internal NodeLocalIdComparer(StringComparer comparer)
    {
        _comparer = comparer;
    }

    public bool Equals(NodeLocalId x, NodeLocalId y) =>
        _comparer.Equals(x.Value, y.Value);

    public int GetHashCode(NodeLocalId obj) =>
        _comparer.GetHashCode(obj.Value);

    public int Compare(NodeLocalId x, NodeLocalId y) =>
        _comparer.Compare(x.Value, y.Value);
}

[JsonConverter(typeof(NodeGlobalIdJsonConverter))]
public readonly struct NodeGlobalId : IReadOnlyList<NodeLocalId>, IEquatable<NodeGlobalId>
{
    private readonly ImmutableArray<NodeLocalId> _segments;
    private readonly int _hashCode;

    public NodeGlobalId(IEnumerable<string> segments)
        : this(CreateSegments(segments))
    {
    }

    public NodeGlobalId(IEnumerable<NodeLocalId> segments)
        : this(CreateSegments(segments))
    {
    }

    public NodeGlobalId(params string[] segments)
        : this((IEnumerable<string>)segments)
    {
    }

    public NodeGlobalId(ImmutableArray<NodeLocalId> segments)
    {
        if (segments.IsDefault)
            segments = ImmutableArray<NodeLocalId>.Empty;

        ValidateSegments(segments);
        _segments = segments;
        _hashCode = CalculateHashCode(segments);
    }

    public static NodeGlobalId Empty => default;

    public ImmutableArray<NodeLocalId> Segments =>
        _segments.IsDefault ? ImmutableArray<NodeLocalId>.Empty : _segments;

    public int Count => Segments.Length;

    public bool IsEmpty => Count == 0;

    public NodeLocalId this[int index] => Segments[index];

    public NodeGlobalId Append(string segment) => Append(new NodeLocalId(segment));

    public NodeGlobalId Append(NodeLocalId segment)
    {
        if (segment.IsEmpty)
            throw new ArgumentException("Path segment must not be empty.", nameof(segment));

        return new NodeGlobalId(Segments.Add(segment));
    }

    public ImmutableArray<NodeLocalId>.Enumerator GetEnumerator() => Segments.GetEnumerator();

    IEnumerator<NodeLocalId> IEnumerable<NodeLocalId>.GetEnumerator() =>
        ((IEnumerable<NodeLocalId>)Segments).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() =>
        ((IEnumerable<NodeLocalId>)Segments).GetEnumerator();

    public bool Equals(NodeGlobalId other)
    {
        var segments = Segments;
        var otherSegments = other.Segments;
        if (segments.Length != otherSegments.Length)
            return false;

        if (_hashCode != other._hashCode)
            return false;

        for (var i = 0; i < segments.Length; i++)
            if (segments[i] != otherSegments[i])
                return false;

        return true;
    }

    public override bool Equals(object? obj) =>
        obj is NodeGlobalId other && Equals(other);

    public override int GetHashCode() => _hashCode;

    public override string ToString() => JoinSegments(Segments);

    public static NodeGlobalId FromSegments(IEnumerable<NodeLocalId> segments) => new(segments);

    public static NodeGlobalId Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var segments = ImmutableArray.CreateBuilder<NodeLocalId>();
        var start = 0;
        while (start < path.Length)
        {
            var separator = path.IndexOf('/', start);
            var length = separator < 0
                ? path.Length - start
                : separator - start;

            if (length > 0)
                segments.Add(new NodeLocalId(path.Substring(start, length)));

            if (separator < 0)
                break;

            start = separator + 1;
        }

        return new NodeGlobalId(segments.MoveToImmutable());
    }

    public static explicit operator NodeGlobalId?(string[]? path) =>
        path is null ? null : new NodeGlobalId(path);

    public static bool operator ==(NodeGlobalId left, NodeGlobalId right) => left.Equals(right);

    public static bool operator !=(NodeGlobalId left, NodeGlobalId right) => !left.Equals(right);

    private static ImmutableArray<NodeLocalId> CreateSegments(IEnumerable<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments is string[] array)
        {
            var builder = ImmutableArray.CreateBuilder<NodeLocalId>(array.Length);
            foreach (var segment in array)
                builder.Add(new NodeLocalId(segment));
            return builder.MoveToImmutable();
        }

        var result = ImmutableArray.CreateBuilder<NodeLocalId>(
            segments is IReadOnlyCollection<string> collection ? collection.Count : 0);
        foreach (var segment in segments)
            result.Add(new NodeLocalId(segment));
        return result.MoveToImmutable();
    }

    private static ImmutableArray<NodeLocalId> CreateSegments(IEnumerable<NodeLocalId> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);

        if (segments is ImmutableArray<NodeLocalId> immutable)
            return immutable;

        var result = ImmutableArray.CreateBuilder<NodeLocalId>(
            segments is IReadOnlyCollection<NodeLocalId> collection ? collection.Count : 0);
        foreach (var segment in segments)
            result.Add(segment);
        return result.MoveToImmutable();
    }

    private static void ValidateSegments(ImmutableArray<NodeLocalId> segments)
    {
        foreach (var segment in segments)
            if (segment.IsEmpty)
                throw new ArgumentException("Path segment must not be empty.", nameof(segments));
    }

    private static int CalculateHashCode(ImmutableArray<NodeLocalId> segments)
    {
        if (segments.IsDefaultOrEmpty)
            return 0;

        var hash = new HashCode();
        foreach (var segment in segments)
            hash.Add(segment);
        return hash.ToHashCode();
    }

    private static string JoinSegments(ImmutableArray<NodeLocalId> segments)
    {
        if (segments.IsDefaultOrEmpty)
            return string.Empty;

        if (segments.Length == 1)
            return segments[0].Value;

        var length = segments.Length - 1;
        foreach (var segment in segments)
            length += segment.Value.Length;

        return string.Create(length, segments, static (destination, source) =>
        {
            var offset = 0;
            for (var i = 0; i < source.Length; i++)
            {
                if (i > 0)
                    destination[offset++] = '/';

                var value = source[i].Value;
                value.AsSpan().CopyTo(destination[offset..]);
                offset += value.Length;
            }
        });
    }
}

public sealed class NodeLocalIdJsonConverter : JsonConverter<NodeLocalId>
{
    public override NodeLocalId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null)
            throw new JsonException("Node local id must be a string.");

        return new NodeLocalId(value);
    }

    public override void Write(
        Utf8JsonWriter writer,
        NodeLocalId value,
        JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.Value);
    }
}

public sealed class NodeGlobalIdJsonConverter : JsonConverter<NodeGlobalId>
{
    public override NodeGlobalId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return NodeGlobalId.Parse(reader.GetString()!);

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Node global id must be a string array.");

        var builder = ImmutableArray.CreateBuilder<NodeLocalId>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
                return new NodeGlobalId(builder.MoveToImmutable());

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Node global id segment must be a string.");

            builder.Add(new NodeLocalId(reader.GetString()!));
        }

        throw new JsonException("Node global id array is incomplete.");
    }

    public override void Write(
        Utf8JsonWriter writer,
        NodeGlobalId value,
        JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var segment in value)
            writer.WriteStringValue(segment.Value);
        writer.WriteEndArray();
    }
}
