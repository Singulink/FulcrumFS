using System.Collections;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;

namespace FulcrumFS.Internals;

/// <summary>
/// Do not use - for internal use only, not for public use.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class EquatableArray<T>(ImmutableArray<T> array) : IReadOnlyList<T>, IList<T>, IEquatable<EquatableArray<T>>
{
    // Stores the hash code, or 0 if not yet calculated.
    // On 64-bit systems, the hash code has 1UL << 32 or'd, which allows the 0 hash code also - whereas on 32-bit 0 is replaced with 1.
    // Note: this field must be accessed carefully to ensure its thread-safety.
    private nint _hashCode;

    // Stores the array - never null.
    // Note: we store the raw array so we can perform volatile operations on it for de-duplicating.
    private T[] _array = array == default ? [] : ImmutableCollectionsMarshal.AsArray(array)!;

    // Helper to store our (approximate) creation index for de-duplicating wisely:
    // Note: this is not thread-safe, but that's okay - it's just an approximation to help with de-duplication preferring older instances.
    private static int _staticCreateIndex;
    private int _createIndex = _staticCreateIndex++;

    /// <summary>
    /// Gets a hash code for the equatable array, which is based on value equality, and is cached.
    /// </summary>
    public override int GetHashCode()
    {
        // Check if we have already calculated the hash code - if so, return it:
        nint value = Volatile.Read(ref _hashCode);
        if (value != 0) return (int)value;

        // Otherwise, calculate it:
        return CalculateHashCode();
    }

    // Helper to calculate the hash code & store it in a thread-safe way:
    private int CalculateHashCode()
    {
        HashCode hc = default;
        hc.Add(_array.Length);
        foreach (T item in _array) hc.Add(item);
        int result = hc.ToHashCode();
        if (IntPtr.Size < 8) result += (result == 0) ? 1 : 0;
        Volatile.Write(ref _hashCode, ((nint)1 << 32) | (nint)(nuint)(uint)result);
        return result;
    }

    // Helper to de-duplicate this instance against a new primary instance:
    private void Deduplicate(EquatableArray<T> newPrimary)
    {
        // Note: none of this needs volatile, as it's all immutable anyway, so we just do a best-effort / low-cost de-duplication:
        _array = newPrimary._array;
        _createIndex = newPrimary._createIndex;
    }

    /// <inheritdoc />
    public bool Equals([NotNullWhen(true)] EquatableArray<T>? other)
    {
        // Check simple cases:
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        T[] otherArray = other._array;
        T[] array = _array;
        if (array == otherArray) return true;
        if (array.Length != otherArray.Length) return false;

        // Compare hash codes first for speed (this will catch 99.99+% of non-equal cases):
        if (GetHashCode() != other.GetHashCode()) return false;

        // Check if all elements are equal:
        for (int i = 0; i < array.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(array[i], otherArray[i])) return false;
        }

        // Equal - perform de-duplication if needed:
        if (_createIndex <= other._createIndex)
        {
            other.Deduplicate(this);
        }
        else
        {
            Deduplicate(other);
        }

        // Return equal:
        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as EquatableArray<T>);

    /// <summary>
    /// Value-based equality operator.
    /// </summary>
    public static bool operator ==(EquatableArray<T>? left, EquatableArray<T>? right) => Equals(left, right);

    /// <summary>
    /// Value-based inequality operator.
    /// </summary>
    public static bool operator !=(EquatableArray<T>? left, EquatableArray<T>? right) => !(left == right);

    /// <inheritdoc />
    public int Count => _array.Length;

    /// <inheritdoc />
    public bool IsReadOnly => true;

    /// <inheritdoc />
    T IList<T>.this[int index]
    {
        get => _array[index];
        set => throw new NotSupportedException("Collection is read-only.");
    }

    /// <inheritdoc />
    T IReadOnlyList<T>.this[int index] => _array[index];

    /// <inheritdoc />
    IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)ImmutableCollectionsMarshal.AsImmutableArray(_array)).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)this).GetEnumerator();

    /// <inheritdoc />
    int IList<T>.IndexOf(T item) => ((IList<T>)_array).IndexOf(item);

    /// <inheritdoc />
    void IList<T>.Insert(int index, T item) => throw new NotSupportedException("Collection is read-only.");

    /// <inheritdoc />
    void IList<T>.RemoveAt(int index) => throw new NotSupportedException("Collection is read-only.");

    /// <inheritdoc />
    void ICollection<T>.Add(T item) => throw new NotSupportedException("Collection is read-only.");

    /// <inheritdoc />
    void ICollection<T>.Clear() => throw new NotSupportedException("Collection is read-only.");

    /// <inheritdoc />
    bool ICollection<T>.Contains(T item) => ((ICollection<T>)_array).Contains(item);

    /// <inheritdoc />
    void ICollection<T>.CopyTo(T[] array, int arrayIndex) => ((ICollection<T>)_array).CopyTo(array, arrayIndex);

    /// <inheritdoc />
    bool ICollection<T>.Remove(T item) => throw new NotSupportedException("Collection is read-only.");

    /// <inheritdoc />
    public override string ToString()
    {
        StringBuilder sb = new();
        sb.Append($"EquatableArray<{typeof(T).Name}>[{_array.Length}]");
        sb.Append(" { ");

        foreach (var item in _array)
        {
            sb.Append(item);
            sb.Append(", ");
        }

        sb.Append(" } ");
        return sb.ToString();
    }
}
