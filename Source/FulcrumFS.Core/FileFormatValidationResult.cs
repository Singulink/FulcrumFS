using System.Diagnostics.CodeAnalysis;

namespace FulcrumFS;

/// <summary>
/// Represents the result of validating a file's content against a <see cref="FileFormat"/>.
/// </summary>
public readonly record struct FileFormatValidationResult
{
    /// <summary>
    /// Gets a <see cref="FileFormatValidationResult"/> representing a successful (valid) validation.
    /// </summary>
    public static FileFormatValidationResult Success { get; } = default;

    /// <summary>
    /// Gets a value indicating whether validation succeeded. Returns <see langword="true"/> if the file content matches the expected file format; otherwise,
    /// <see langword="false"/>, in which case <see cref="ErrorMessage"/> contains a description of why validation failed.
    /// </summary>
    [MemberNotNullWhen(false, nameof(ErrorMessage))]
    public bool IsValid => ErrorMessage is null;

    /// <summary>
    /// Gets the error message describing why validation failed, or <see langword="null"/> if validation succeeded.
    /// </summary>
    public string? ErrorMessage { get; }

    private FileFormatValidationResult(string errorMessage)
    {
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Creates a <see cref="FileFormatValidationResult"/> representing a failed (invalid) validation with the specified error message.
    /// </summary>
    /// <param name="errorMessage">A non-empty message describing why validation failed.</param>
    public static FileFormatValidationResult Invalid(string errorMessage)
    {
        ArgumentException.ThrowIfNullOrEmpty(errorMessage);
        return new FileFormatValidationResult(errorMessage);
    }

    /// <inheritdoc/>
    public override string ToString() => IsValid ? "Valid" : $"Invalid: {ErrorMessage}";
}
