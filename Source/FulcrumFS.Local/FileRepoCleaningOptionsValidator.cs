using Microsoft.Extensions.Options;

namespace FulcrumFS;

/// <summary>
/// Validates <see cref="FileRepoCleaningOptions"/> for the options system with the same rules as <see cref="FileRepoCleaningOptions.Validate"/> and the
/// <see cref="FileRepoCleaner"/> constructor, reporting every problem. It checks options of every name.
/// <see cref="FileRepoServiceCollectionExtensions.AddFileRepoCleaner"/> registers it.
/// </summary>
internal sealed class FileRepoCleaningOptionsValidator : IValidateOptions<FileRepoCleaningOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, FileRepoCleaningOptions options)
    {
        var problems = options.GetProblems();
        return problems.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(problems);
    }
}
