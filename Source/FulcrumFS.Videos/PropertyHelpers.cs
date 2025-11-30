using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace FulcrumFS.Videos;

/// <summary>
/// Provides helper methods for property getters in configs. These help with implementing our lazy copy constructors.
/// Note: the current setup does not include logic to "free" the base config reference once all properties have been initialized,
/// as this would add unnecessary complexity and is not currently needed for our expected usage scenarios.
/// </summary>
internal static class PropertyHelpers
{
    /// <summary>
    /// Gets the value for a helper-bool-backed property, using the base config if the property was not overridden and the property has not been
    /// cached yet. The isFieldInitialized helper bool is used to track whether the field has been initialized. If a value is still unavailable (due to
    /// <paramref name="baseConfig" /> being <see langword="null" />), then <paramref name="defaultValue" /> is used for the value.
    /// Note: this overload is primarily intended for <see langword="struct" />-typed properties and <see cref="T:System.Nullable`1"/>-typed properties.
    /// Note: the delegate provided for <paramref name="baseGetter" /> should be a static lambda to avoid allocating a closure unnecessarily.
    /// </summary>
    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static T? GetHelper<TConfig, T>(
        TConfig? baseConfig,
        Func<TConfig, T> baseGetter,
        ref T? field,
        ref bool isFieldInitialized,
        T? defaultValue = default)
        where TConfig : class
    {
        if (!Volatile.Read(in isFieldInitialized))
        {
            return Fallback(baseConfig!, baseGetter, ref field, ref isFieldInitialized, defaultValue);

            // Rare case is outlined to local function to improve chance of optimal inlining.
            [return: NotNullIfNotNull(nameof(defaultValue))]
            static T? Fallback(TConfig baseConfig, Func<TConfig, T> baseGetter, ref T? field, ref bool isFieldInitialized, T? defaultValue)
            {
                T localValue = baseConfig is not null ? baseGetter(baseConfig) : defaultValue;
                field = localValue;
                Volatile.Write(ref isFieldInitialized, true);
                return localValue;
            }
        }

        return field;
    }

    /// <summary>
    /// Gets the value for a <see langword="class" />-typed property, using the base config if the property was not overridden and the property has not been
    /// cached yet. If a value is still unavailable (due to <paramref name="baseConfig" /> being <see langword="null" />), then
    /// <paramref name="defaultValue" /> is used for the value.
    /// Note: the delegate provided for <paramref name="baseGetter" /> should be a static lambda to avoid allocating a closure unnecessarily.
    /// </summary>
    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static T? GetHelper<TConfig, T>(TConfig? baseConfig, Func<TConfig, T> baseGetter, ref T? field, T? defaultValue = null)
        where TConfig : class
        where T : class?
    {
        T? localValue = field;

        if (localValue is null)
        {
            return Fallback(baseConfig, baseGetter, ref field, defaultValue);

            // Rare case is outlined to local function to improve chance of optimal inlining.
            [return: NotNullIfNotNull(nameof(defaultValue))]
            static T? Fallback(TConfig? baseConfig, Func<TConfig, T> baseGetter, ref T? field, T? defaultValue)
            {
                T? localValue = baseConfig is not null ? baseGetter(baseConfig) : defaultValue;
                field = localValue;
                return localValue;
            }
        }

        return localValue;
    }

    /// <summary>
    /// Helper for the user accessible <see langword="init" /> accessor for the property getter of helper-bool-backed property.
    /// The init-ing counterpart to <see cref="M:FulcrumFS.Videos.PropertyHelpers.GetHelper``2(``0,System.Func{``0,``1},``1@,System.Boolean@,``1)" />.
    /// </summary>
    public static void InitHelper<T>(ref T? field, T value, ref bool isFieldInitialized)
    {
        field = value;
        isFieldInitialized = true;

        // Note: the reason we need a Volatile write barrier here is because otherwise if we were to send the containing object to another thread just after we
        // initialized it, there would be nothing stopping it from seeing a partially uninitialized value. The write barrier ensures that writes after
        // it cannot be reordered to before it. This is a no-op on x86/x64 (i.e., it only inhibits runtime optimizations). Note: we do not need to make the
        // write to isFieldInitialized volatile itself, as it is only possible to get here during initialization, and the object can only be shared after.
        Volatile.WriteBarrier();
    }

    private static readonly CompositeFormat _checkFieldInitializedErrorMessageFormat = CompositeFormat.Parse(
        "Cannot read property '{0}' as it is an uninitialized property on an incomplete VideoStreamProcessingOptions object. " +
        "Please use the copy constructor on a pre-existing full object, or assign this object to VideoProcessor.VideoStreamOptions " +
        "(which will assign the new full object) to create a full object.");

    // Helper method to check if the object is fully initialized for reading.
    [StackTraceHidden]
    public static void CheckFieldInitialized(bool isInitialized, [CallerMemberName] string? propertyName = null)
    {
        if (!isInitialized)
        {
            [DoesNotReturn]
            static void Throw(string? propertyName)
            {
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, _checkFieldInitializedErrorMessageFormat, propertyName));
            }

            Throw(propertyName);
        }
    }
}
