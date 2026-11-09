using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FulcrumFS;

[PrefixTestClass]
public sealed class FileRepoOptionsTests
{
    private static readonly IAbsoluteDirectoryPath _appDir = DirectoryPath.GetAppBase();

    private static int _counter;

    // ---------------------------------------------------------------------------------------------------------------
    // FileRepoOptions
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void RepoOptions_NeverSet_ReadAsUnset_AndValidateNamesBoth_WithoutFreezing()
    {
        // Options bound from configuration are created without the required members, as here.
        var options = Activator.CreateInstance<FileRepoOptions>();

        options.BaseDirectory.ShouldBeNull();
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Unspecified);
        options.IsFrozen.ShouldBeFalse();

        var ex = Should.Throw<InvalidOperationException>(options.Validate);
        ex.Message.ShouldBe(
            "FileRepoOptions.BaseDirectory is not set. It is required, and has no default. FileRepoOptions.MinimumFormatVersion must be set to Version1, " +
            "Version2 or Latest; Version1 keeps repositories compatible with FulcrumFS 1.0.x.");

        options.IsFrozen.ShouldBeFalse();

        // They can still be set, in any order, after which they are valid.
        options.MinimumFormatVersion = RepoFormatVersion.Version2;
        options.BaseDirectory = NewRepoDir();
        options.Validate();
        options.IsFrozen.ShouldBeFalse();
    }

    [TestMethod]
    public void RepoOptions_NeverSet_RepositoryConstructionThrows_ListingEveryProblem_WithoutFreezing()
    {
        var options = Activator.CreateInstance<FileRepoOptions>();

        var ex = Should.Throw<ArgumentException>(() => new FileRepo(options));
        ex.ParamName.ShouldBe("options");
        ex.Message.ShouldStartWith("FileRepoOptions.BaseDirectory is not set. It is required, and has no default. FileRepoOptions.MinimumFormatVersion");
        options.IsFrozen.ShouldBeFalse();

        ex = Should.Throw<ArgumentException>(() => new FileRepo(Microsoft.Extensions.Options.Options.Create(options)));
        ex.ParamName.ShouldBe("options");
        ex.Message.ShouldStartWith("FileRepoOptions.BaseDirectory is not set.");

        options.BaseDirectory = NewRepoDir();
        ex = Should.Throw<ArgumentException>(() => new FileRepo(options));
        ex.Message.ShouldStartWith("FileRepoOptions.MinimumFormatVersion must be set to Version1, Version2 or Latest;");
        ex.Message.ShouldNotContain("BaseDirectory");
    }

    [TestMethod]
    public void RepoOptions_FormatVersion_UnspecifiedIsRejected_ExceptAsTheValueItAlreadyHas()
    {
        var unset = Activator.CreateInstance<FileRepoOptions>();

        // The configuration binder writes back the value it read, which leaves the value unset.
        unset.MinimumFormatVersion = RepoFormatVersion.Unspecified;
        unset.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Unspecified);

        var options = new FileRepoOptions(NewRepoDir(), RepoFormatVersion.Version1);
        Should.Throw<ArgumentOutOfRangeException>(() => options.MinimumFormatVersion = RepoFormatVersion.Unspecified).ParamName.ShouldBe("value");
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Version1);

        Should.Throw<ArgumentOutOfRangeException>(() => new FileRepoOptions(NewRepoDir(), RepoFormatVersion.Unspecified))
            .ParamName.ShouldBe("minimumFormatVersion");
        Should.Throw<ArgumentOutOfRangeException>(() => new FileRepo(NewRepoDir(), RepoFormatVersion.Unspecified))
            .ParamName.ShouldBe("minimumFormatVersion");
    }

    [TestMethod]
    public void RepoOptions_FrozenByRepository_SettersThrow_NamingTheClass()
    {
        var options = new FileRepoOptions(NewRepoDir(), RepoFormatVersion.Version2);
        options.IsFrozen.ShouldBeFalse();

        using var repo = new FileRepo(options);
        options.IsFrozen.ShouldBeTrue();
        options.Validate();

        const string message = "The FileRepoOptions are in use by a FileRepo, so they can no longer be changed.";

        Should.Throw<InvalidOperationException>(() => options.BaseDirectory = NewRepoDir()).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.MinimumFormatVersion = RepoFormatVersion.Version2).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.DeleteMode = DeleteMode.Immediate).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.FileIdMode = FileIdMode.Sequential).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.HealthCheckInterval = TimeSpan.FromSeconds(5)).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.MarkerFileLogging = RepoLoggingMode.None).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.MaxAccessWaitOrRetryTime = TimeSpan.FromSeconds(5)).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.ReadOnly = true).Message.ShouldBe(message);

        // A second repository on the same options leaves them as they are.
        using var second = new FileRepo(options);
        second.Options.ShouldBeSameAs(options);
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Version2);
    }

    [TestMethod]
    public void RepoOptions_BoundFromConfiguration_WithoutTheFormatVersion_RepositoryConstructionNamesIt()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Repo:DeleteMode"] = "Immediate",
                ["Repo:HealthCheckInterval"] = "00:00:30",
            })
            .Build();

        // Binding reads the getters of the keys that are missing and writes the values back, which must not throw.
        var options = configuration.GetSection("Repo").Get<FileRepoOptions>().ShouldNotBeNull();
        options.DeleteMode.ShouldBe(DeleteMode.Immediate);
        options.HealthCheckInterval.ShouldBe(TimeSpan.FromSeconds(30));
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Unspecified);

        options.BaseDirectory = NewRepoDir();

        var ex = Should.Throw<ArgumentException>(() => new FileRepo(options));
        ex.Message.ShouldStartWith("FileRepoOptions.MinimumFormatVersion must be set to Version1, Version2 or Latest;");

        // Binding again leaves the version unset, and a version in the configuration sets it.
        configuration.GetSection("Repo").Bind(options);
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Unspecified);

        configuration["Repo:MinimumFormatVersion"] = "Version2";
        configuration.GetSection("Repo").Bind(options);
        options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Version2);

        using var repo = new FileRepo(options);
        options.IsFrozen.ShouldBeTrue();
    }

    [TestMethod]
    public void RepoOptions_BaseDirectory_UnfriendlyNamesAreRejected_FriendlyAccepted()
    {
        var friendly = NewRepoDir();
        var options = new FileRepoOptions(friendly, RepoFormatVersion.Version2);

        foreach (var (name, error) in UnfriendlyNames)
        {
            var dir = _appDir.CombineDirectory(name, PathOptions.None);
            string message = $"FileRepoOptions.BaseDirectory '{dir.PathDisplay}' must be a path with friendly names: {error}";

            var ex = Should.Throw<ArgumentException>(() => options.BaseDirectory = dir);
            ex.ParamName.ShouldBe("value");
            ex.Message.ShouldStartWith(message);

            ex = Should.Throw<ArgumentException>(() => new FileRepoOptions(dir, RepoFormatVersion.Version2));
            ex.ParamName.ShouldBe("baseDirectory");
            ex.Message.ShouldStartWith(message);

            ex = Should.Throw<ArgumentException>(() => new FileRepo(dir, RepoFormatVersion.Version2));
            ex.ParamName.ShouldBe("baseDirectory");
            ex.Message.ShouldStartWith(message);
        }

        // A rejected value leaves the options as they were.
        options.BaseDirectory.ShouldBe(friendly);
        options.IsFrozen.ShouldBeFalse();

        var other = NewRepoDir();
        options.BaseDirectory = other;
        options.BaseDirectory.ShouldBe(other);

        using var repo = new FileRepo(options);
        options.IsFrozen.ShouldBeTrue();

        // Frozen options throw for any value, as before.
        Should.Throw<InvalidOperationException>(() => options.BaseDirectory = _appDir.CombineDirectory("repo ", PathOptions.None))
            .Message.ShouldBe("The FileRepoOptions are in use by a FileRepo, so they can no longer be changed.");
    }

    [TestMethod]
    public void RepoOptions_BaseDirectory_BoundFromConfiguration_FriendlyIsSet_UnfriendlyThrows()
    {
        var repoDir = NewRepoDir();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Repo:BaseDirectory"] = repoDir.PathDisplay,
                ["Repo:MinimumFormatVersion"] = "Version2",
            })
            .Build();

        var options = configuration.GetSection("Repo").Get<FileRepoOptions>().ShouldNotBeNull();
        options.BaseDirectory.ShouldBe(repoDir);

        // The binder reads the value with PathOptions.None, which keeps the trailing space, and the setter rejects it.
        var typo = _appDir.CombineDirectory(repoDir.Name + " ", PathOptions.None);
        configuration["Repo:BaseDirectory"] = typo.PathDisplay;

        var ex = Should.Throw<TargetInvocationException>(() => configuration.GetSection("Repo").Bind(options));
        ex.InnerException.ShouldBeOfType<ArgumentException>().Message
            .ShouldStartWith($"FileRepoOptions.BaseDirectory '{typo.PathDisplay}' must be a path with friendly names: Entry name ends with a space.");

        options.BaseDirectory.ShouldBe(repoDir);

        // Registered with AddFileRepo, the typo stops the application at startup.
        var services = new ServiceCollection().AddFileRepo(configuration.GetSection("Repo").Bind);
        using var provider = services.BuildServiceProvider();

        Should.Throw<TargetInvocationException>(provider.GetRequiredService<IStartupValidator>().Validate)
            .InnerException.ShouldBeOfType<ArgumentException>().ParamName.ShouldBe("value");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // FileRepoCleaningOptions
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    public void CleaningOptions_NeverSet_ValidateAndCleanerConstructionNameIt_WithoutFreezing()
    {
        var options = Activator.CreateInstance<FileRepoCleaningOptions>();
        options.BaseDirectory.ShouldBeNull();
        options.IsFrozen.ShouldBeFalse();

        const string message = "FileRepoCleaningOptions.BaseDirectory is not set. It is required, and has no default.";

        Should.Throw<InvalidOperationException>(options.Validate).Message.ShouldBe(message);

        var ex = Should.Throw<ArgumentException>(() => new FileRepoCleaner(options));
        ex.ParamName.ShouldBe("options");
        ex.Message.ShouldStartWith(message);

        ex = Should.Throw<ArgumentException>(() => new FileRepoCleaner(Microsoft.Extensions.Options.Options.Create(options)));
        ex.ParamName.ShouldBe("options");
        ex.Message.ShouldStartWith(message);

        options.IsFrozen.ShouldBeFalse();
        options.BaseDirectory = NewRepoDir();
        options.Validate();
    }

    [TestMethod]
    public void CleaningOptions_FrozenByCleaner_SettersThrow_NamingTheClass()
    {
        var options = new FileRepoCleaningOptions(NewRepoDir());
        options.IsFrozen.ShouldBeFalse();

        _ = new FileRepoCleaner(options);
        options.IsFrozen.ShouldBeTrue();
        options.Validate();

        const string message = "The FileRepoCleaningOptions are in use by a FileRepoCleaner, so they can no longer be changed.";

        Should.Throw<InvalidOperationException>(() => options.BaseDirectory = NewRepoDir()).Message.ShouldBe(message);
        Should.Throw<InvalidOperationException>(() => options.MarkerFileLogging = RepoLoggingMode.None).Message.ShouldBe(message);
    }

    [TestMethod]
    public void CleaningOptions_BaseDirectory_UnfriendlyNamesAreRejected_FriendlyAccepted()
    {
        var friendly = NewRepoDir();
        var options = new FileRepoCleaningOptions(friendly);

        foreach (var (name, error) in UnfriendlyNames)
        {
            var dir = _appDir.CombineDirectory(name, PathOptions.None);
            string message = $"FileRepoCleaningOptions.BaseDirectory '{dir.PathDisplay}' must be a path with friendly names: {error}";

            var ex = Should.Throw<ArgumentException>(() => options.BaseDirectory = dir);
            ex.ParamName.ShouldBe("value");
            ex.Message.ShouldStartWith(message);

            ex = Should.Throw<ArgumentException>(() => new FileRepoCleaningOptions(dir));
            ex.ParamName.ShouldBe("baseDirectory");
            ex.Message.ShouldStartWith(message);
        }

        // A rejected value leaves the options as they were.
        options.BaseDirectory.ShouldBe(friendly);
        options.IsFrozen.ShouldBeFalse();

        var other = NewRepoDir();
        options.BaseDirectory = other;
        options.BaseDirectory.ShouldBe(other);

        _ = new FileRepoCleaner(options);
        options.IsFrozen.ShouldBeTrue();

        // Frozen options throw for any value, as before.
        Should.Throw<InvalidOperationException>(() => options.BaseDirectory = _appDir.CombineDirectory("repo ", PathOptions.None))
            .Message.ShouldBe("The FileRepoCleaningOptions are in use by a FileRepoCleaner, so they can no longer be changed.");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Both
    // ---------------------------------------------------------------------------------------------------------------

    [TestMethod]
    [DataRow(typeof(FileRepoOptions))]
    [DataRow(typeof(FileRepoCleaningOptions))]
    public void Sealed_BaseDirectoryIsAnnotatedAsNullWhileUnset_AndTakesNoNull(Type optionsType)
    {
        optionsType.IsSealed.ShouldBeTrue();

        var info = new NullabilityInfoContext().Create(optionsType.GetProperty("BaseDirectory")!);
        info.ReadState.ShouldBe(NullabilityState.Nullable);
        info.WriteState.ShouldBe(NullabilityState.NotNull);

        // The setter keeps null out, and so does the constructor.
        object options = Activator.CreateInstance(optionsType)!;
        var ex = Should.Throw<TargetInvocationException>(() => optionsType.GetProperty("BaseDirectory")!.SetValue(options, null));
        ex.InnerException.ShouldBeOfType<ArgumentNullException>();
    }

    [TestMethod]
    public void Validators_ReportEveryProblem_OfOptionsOfEveryName()
    {
        var validator = new FileRepoOptionsValidator();

        var result = validator.Validate("Media", Activator.CreateInstance<FileRepoOptions>());
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeNull().Count().ShouldBe(2);
        result.FailureMessage.ShouldStartWith(
            "FileRepoOptions.BaseDirectory is not set. It is required, and has no default.; FileRepoOptions.MinimumFormatVersion");

        validator.Validate(Options.DefaultName, new FileRepoOptions(NewRepoDir(), RepoFormatVersion.Version2)).Succeeded.ShouldBeTrue();

        var cleaningValidator = new FileRepoCleaningOptionsValidator();
        cleaningValidator.Validate(null, Activator.CreateInstance<FileRepoCleaningOptions>()).FailureMessage
            .ShouldBe("FileRepoCleaningOptions.BaseDirectory is not set. It is required, and has no default.");
        cleaningValidator.Validate(null, new FileRepoCleaningOptions(NewRepoDir())).Succeeded.ShouldBeTrue();
    }

    [TestMethod]
    public void AddFileRepo_ResolvesOneRepository_AsEveryService_AndFreezesTheOptionsWhenItIsCreated()
    {
        var repoDir = NewRepoDir();
        var services = new ServiceCollection();
        services.AddFileRepo(o => o.BaseDirectory = repoDir);
        services.AddFileRepo(o => o.MinimumFormatVersion = RepoFormatVersion.Version2);

        // Calling it twice adds the second action and nothing else.
        services.Count(d => d.ServiceType == typeof(FileRepo)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<FileRepoOptions>)).ShouldBe(1);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        var options = provider.GetRequiredService<IOptions<FileRepoOptions>>().Value;
        options.IsFrozen.ShouldBeFalse();

        var repo = provider.GetRequiredService<FileRepo>();
        options.IsFrozen.ShouldBeTrue();
        repo.Options.ShouldBeSameAs(options);
        repo.BaseDirectory.ShouldBe(repoDir);
        repo.Options.MinimumFormatVersion.ShouldBe(RepoFormatVersion.Version2);

        provider.GetRequiredService<IFileRepo>().ShouldBeSameAs(repo);
        provider.GetRequiredService<IFileRepoCleaner>().ShouldBeSameAs(repo);
    }

    [TestMethod]
    public void AddFileRepo_InvalidOptions_FailStartupValidation_ListingEveryProblem()
    {
        var services = new ServiceCollection();
        services.AddFileRepo(o => o.DeleteMode = DeleteMode.Immediate);

        using var provider = services.BuildServiceProvider();

        var ex = Should.Throw<OptionsValidationException>(provider.GetRequiredService<IStartupValidator>().Validate);
        ex.OptionsType.ShouldBe(typeof(FileRepoOptions));
        ex.Failures.Count().ShouldBe(2);
        ex.Failures.First().ShouldBe("FileRepoOptions.BaseDirectory is not set. It is required, and has no default.");
        ex.Failures.Last().ShouldStartWith("FileRepoOptions.MinimumFormatVersion must be set to Version1, Version2 or Latest;");
    }

    [TestMethod]
    public void AddFileRepoCleaner_ResolvesOneCleaner_AsEveryService_AndFreezesTheOptionsWhenItIsCreated()
    {
        var repoDir = NewRepoDir();
        var services = new ServiceCollection();
        services.AddFileRepoCleaner(o => o.BaseDirectory = repoDir);
        services.AddFileRepoCleaner();

        services.Count(d => d.ServiceType == typeof(FileRepoCleaner)).ShouldBe(1);
        services.Count(d => d.ServiceType == typeof(IValidateOptions<FileRepoCleaningOptions>)).ShouldBe(1);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IStartupValidator>().Validate();

        var options = provider.GetRequiredService<IOptions<FileRepoCleaningOptions>>().Value;
        options.IsFrozen.ShouldBeFalse();

        var cleaner = provider.GetRequiredService<FileRepoCleaner>();
        options.IsFrozen.ShouldBeTrue();
        cleaner.Options.ShouldBeSameAs(options);
        cleaner.BaseDirectory.ShouldBe(repoDir);

        provider.GetRequiredService<IFileRepoCleaner>().ShouldBeSameAs(cleaner);
    }

    [TestMethod]
    public void AddFileRepoCleaner_InvalidOptions_FailStartupValidation_ListingEveryProblem()
    {
        var services = new ServiceCollection();
        services.AddFileRepoCleaner();

        using var provider = services.BuildServiceProvider();

        var ex = Should.Throw<OptionsValidationException>(provider.GetRequiredService<IStartupValidator>().Validate);
        ex.OptionsType.ShouldBe(typeof(FileRepoCleaningOptions));
        ex.Failures.ShouldBe(["FileRepoCleaningOptions.BaseDirectory is not set. It is required, and has no default."]);
    }

    [TestMethod]
    public void AddFileRepo_AndAddFileRepoCleaner_TheFirstCallProvidesIFileRepoCleaner()
    {
        var repoDir = NewRepoDir();

        var repoFirst = new ServiceCollection()
            .AddFileRepo(o => {
                o.BaseDirectory = repoDir;
                o.MinimumFormatVersion = RepoFormatVersion.Version2;
            })
            .AddFileRepoCleaner(o => o.BaseDirectory = repoDir);

        using (var provider = repoFirst.BuildServiceProvider())
            provider.GetRequiredService<IFileRepoCleaner>().ShouldBeSameAs(provider.GetRequiredService<FileRepo>());

        var cleanerFirst = new ServiceCollection()
            .AddFileRepoCleaner(o => o.BaseDirectory = repoDir)
            .AddFileRepo(o => {
                o.BaseDirectory = repoDir;
                o.MinimumFormatVersion = RepoFormatVersion.Version2;
            });

        using (var provider = cleanerFirst.BuildServiceProvider())
            provider.GetRequiredService<IFileRepoCleaner>().ShouldBeSameAs(provider.GetRequiredService<FileRepoCleaner>());
    }

    // Names that PathOptions.NoUnfriendlyNames rejects, with the error that IsValidFor gives for them. Reserved device names are only unfriendly on Windows.
    private static (string Name, string Error)[] UnfriendlyNames => OperatingSystem.IsWindows()
        ? [("repo ", "Entry name ends with a space."), ("repo.", "Entry name ends with a dot."), ("CON", "Invalid reserved device name in entry name 'CON'.")]
        : [("repo ", "Entry name ends with a space."), ("repo.", "Entry name ends with a dot.")];

    private static IAbsoluteDirectoryPath NewRepoDir()
    {
        var dir = _appDir.CombineDirectory("RepoRoot_FileRepoOptions_" + Interlocked.Increment(ref _counter).ToString(CultureInfo.InvariantCulture));

        if (dir.Exists)
            dir.Delete(true);

        dir.Create();
        return dir;
    }
}
