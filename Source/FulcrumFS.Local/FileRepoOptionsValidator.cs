using Microsoft.Extensions.Options;

namespace FulcrumFS;

/// <summary>
/// Validates <see cref="FileRepoOptions"/> for the options system with the same rules as <see cref="FileRepoOptions.Validate"/> and the
/// <see cref="FileRepo"/> constructor, reporting every problem. It checks options of every name.
/// <see cref="FileRepoServiceCollectionExtensions.AddFileRepo"/> registers it.
/// </summary>
internal sealed class FileRepoOptionsValidator : IValidateOptions<FileRepoOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, FileRepoOptions options)
    {
        var problems = options.GetProblems();
        return problems.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
