global using InternalId = Abstractions.NodeRef.InternalId;
global using NodePath = Abstractions.NodeRef.NodePath;
using System.Collections;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Abstractions;

public readonly struct NodeLocalId : IEquatable<NodeLocalId>, IComparable<NodeLocalId> {
    readonly string value;

    public NodeLocalId() : this(string.Empty) { }
    public NodeLocalId(string value) => this.value = value;

    public bool Equals(NodeLocalId other) => string.Equals(value, other.value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is NodeLocalId other && Equals(other);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(value);
    public override string ToString() => value;

    public static bool operator ==(NodeLocalId left, NodeLocalId right) => left.Equals(right);
    public static bool operator !=(NodeLocalId left, NodeLocalId right) => !left.Equals(right);
    public static implicit operator string(NodeLocalId id) => id.ToString();
    public static implicit operator NodeLocalId(string id) => new(id);
    public static implicit operator NodeLocalId?(string? id) => id == null ? null : new(id);

    public int CompareTo(NodeLocalId other) => string.Compare(value, other.value, StringComparison.Ordinal);

    internal static NodeLocalId Random() => new NodeLocalId(Guid.NewGuid().ToString());//TODO переписать на создание Id внутри где можно проверить занятость
}
public abstract class NodeRef {
    [Obsolete("не надо использовать ни для чего кроме дедупликации")]
    public sealed class InternalId : NodeRef, IEnumerable<NodeLocalId>, IEquatable<InternalId> {
        readonly ImmutableArray<NodeLocalId> segments;
        readonly int hashCode;

        internal InternalId(params IEnumerable<NodeLocalId> segments) : this(CreateSegments(segments)) { }
        internal InternalId(ImmutableArray<NodeLocalId> segments) {
            if (segments.IsDefault)
                segments = ImmutableArray<NodeLocalId>.Empty;
            this.segments = segments;
            hashCode = CalculateHashCode();
            int CalculateHashCode() {
                if (segments.IsDefaultOrEmpty)
                    return 0;
                var hash = new HashCode();
                foreach (var segment in segments)
                    hash.Add(segment);
                return hash.ToHashCode();
            }
        }

        public bool Equals(InternalId? other) {
            if (other is null)
                return false;
            var otherSegments = other.segments;
            if (segments.Length != otherSegments.Length)
                return false;
            if (hashCode != other.hashCode)
                return false;
            for (var i = 0; i < segments.Length; i++)
                if (segments[i] != otherSegments[i])
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is InternalId other && Equals(other);
        public override int GetHashCode() => hashCode;
        public override string ToString() {
            if (segments.IsDefaultOrEmpty)
                return string.Empty;
            if (segments.Length == 1)
                return segments[0].ToString();
            var length = segments.Length - 1;
            foreach (var segment in segments)
                length += segment.ToString().Length;
            return string.Create(length, segments, static (destination, source) => {
                var offset = 0;
                for (var i = 0; i < source.Length; i++) {
                    if (i > 0)
                        destination[offset++] = '/';
                    var value = source[i].ToString();
                    value.AsSpan().CopyTo(destination[offset..]);
                    offset += value.Length;
                }
            });
        }

        public static explicit operator InternalId?(string[]? path) => path is null ? null : new InternalId(path.Select(x => new NodeLocalId(x)));
        public static implicit operator string(InternalId id) => id.ToString();
        public static bool operator ==(InternalId left, InternalId? right) => left.Equals(right);
        public static bool operator !=(InternalId left, InternalId? right) => !left.Equals(right);

        IEnumerator<NodeLocalId> IEnumerable<NodeLocalId>.GetEnumerator() => ((IEnumerable<NodeLocalId>)segments).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<NodeLocalId>)segments).GetEnumerator();

        static ImmutableArray<NodeLocalId> CreateSegments(IEnumerable<NodeLocalId> segments) {
            if (segments is ImmutableArray<NodeLocalId> immutable)
                return immutable;
            var result = ImmutableArray.CreateBuilder<NodeLocalId>(
                segments is IReadOnlyCollection<NodeLocalId> collection ? collection.Count : 0);
            foreach (var segment in segments)
                result.Add(segment);
            return result.ToImmutable();
        }
    }
    public sealed class NodePath : NodeRef, IEnumerable<NodeLocalId>, IEquatable<NodePath> {
        readonly ImmutableArray<NodeLocalId> segments;
        readonly int hashCode;

        public NodePath() : this(Array.Empty<NodeLocalId>()) { }
        public NodePath(params IEnumerable<string> segments) : this(CreateSegments(segments.Select(x => new NodeLocalId(x)))) { }
        public NodePath(params IEnumerable<NodeLocalId> segments) : this(CreateSegments(segments)) { }
        public NodePath(ImmutableArray<NodeLocalId> segments) {
            if (segments.IsDefault)
                segments = ImmutableArray<NodeLocalId>.Empty;
            this.segments = segments;
            hashCode = CalculateHashCode();
            int CalculateHashCode() {
                if (segments.IsDefaultOrEmpty)
                    return 0;
                var hash = new HashCode();
                foreach (var segment in segments)
                    hash.Add(segment);
                return hash.ToHashCode();
            }
        }

        public bool Equals(NodePath? other) {
            if (other is null)
                return false;
            var otherSegments = other.segments;
            if (segments.Length != otherSegments.Length)
                return false;
            if (hashCode != other.hashCode)
                return false;
            for (var i = 0; i < segments.Length; i++)
                if (segments[i] != otherSegments[i])
                    return false;
            return true;
        }

        public override bool Equals(object? obj) => obj is NodePath other && Equals(other);
        public override int GetHashCode() => hashCode;
        public override string ToString() {
            if (segments.IsDefaultOrEmpty)
                return string.Empty;
            if (segments.Length == 1)
                return segments[0].ToString();
            var length = segments.Length - 1;
            foreach (var segment in segments)
                length += segment.ToString().Length;
            return string.Create(length, segments, static (destination, source) => {
                var offset = 0;
                for (var i = 0; i < source.Length; i++) {
                    if (i > 0)
                        destination[offset++] = '/';
                    var value = source[i].ToString();
                    value.AsSpan().CopyTo(destination[offset..]);
                    offset += value.Length;
                }
            });
        }

        public static explicit operator NodePath?(string[]? path) => path is null ? null : new NodePath(path);
        public static bool operator ==(NodePath left, NodePath right) => left.Equals(right);
        public static bool operator !=(NodePath left, NodePath right) => !left.Equals(right);

        IEnumerator<NodeLocalId> IEnumerable<NodeLocalId>.GetEnumerator() => ((IEnumerable<NodeLocalId>)segments).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<NodeLocalId>)segments).GetEnumerator();

        static ImmutableArray<NodeLocalId> CreateSegments(IEnumerable<NodeLocalId> segments) {
            if (segments is ImmutableArray<NodeLocalId> immutable)
                return immutable;
            var result = ImmutableArray.CreateBuilder<NodeLocalId>(
                segments is IReadOnlyCollection<NodeLocalId> collection ? collection.Count : 0);
            foreach (var segment in segments)
                result.Add(segment);
            return result.ToImmutable();
        }

        public static implicit operator NodePath(InternalId id) => new NodePath(id.ToArray());
    }
}