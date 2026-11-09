using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace FulcrumFS;

/// <summary>
/// Extension methods for registering a <see cref="FileRepo"/> or a <see cref="FileRepoCleaner"/> in an <see cref="IServiceCollection"/>.
/// </summary>
public static class FileRepoServiceCollectionExtensions
{
    /// <summary>
    /// Registers a <see cref="FileRepo"/> as a singleton, created from the <see cref="FileRepoOptions"/> of the options system, and as the
    /// <see cref="IFileRepo"/> and <see cref="IFileRepoCleaner"/> of the application.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options. <see cref="FileRepoOptions.BaseDirectory"/> and
    /// <see cref="FileRepoOptions.MinimumFormatVersion"/> must be set, here or through configuration.</param>
    /// <remarks>
    /// <para>
    /// The options are validated when the application starts and frozen when the repository is created, the first time one of these services is resolved.
    /// The application fails to start if the options are not valid, such as when a required option is not set. The <see cref="OptionsValidationException"/>
    /// lists every problem.</para>
    /// <para>
    /// A service that is already registered is kept, so calling this again only adds <paramref name="configure"/>, and <see cref="IFileRepoCleaner"/> stays
    /// a <see cref="FileRepoCleaner"/> that was registered first (see <see cref="AddFileRepoCleaner"/>). The repository is not opened: the application calls
    /// <see cref="FileRepo.EnsureCreated"/> or <see cref="FileRepo.EnsureInitialized"/> itself. The service provider disposes the repository when it is
    /// disposed.</para>
    /// </remarks>
    public static IServiceCollection AddFileRepo(this IServiceCollection services, Action<FileRepoOptions>? configure = null)
    {
        services.AddOptions<FileRepoOptions>().ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FileRepoOptions>, FileRepoOptionsValidator>());
        services.TryAddSingleton(provider => new FileRepo(provider.GetRequiredService<IOptions<FileRepoOptions>>()));
        services.TryAddSingleton<IFileRepo>(provider => provider.GetRequiredService<FileRepo>());
        services.TryAddSingleton<IFileRepoCleaner>(provider => provider.GetRequiredService<FileRepo>());

        return services;
    }

    /// <summary>
    /// Registers a <see cref="FileRepoCleaner"/> as a singleton, created from the <see cref="FileRepoCleaningOptions"/> of the options system, and as the
    /// <see cref="IFileRepoCleaner"/> of the application.
    /// </summary>
    /// <param name="services">The service collection to add the services to.</param>
    /// <param name="configure">An optional action to configure options. <see cref="FileRepoCleaningOptions.BaseDirectory"/> must be set, here or through
    /// configuration.</param>
    /// <remarks>
    /// <para>
    /// The options are validated when the application starts and frozen when the cleaner is created, the first time one of these services is resolved. The
    /// application fails to start if the options are not valid, such as when the base directory is not set. The <see cref="OptionsValidationException"/>
    /// lists every problem.</para>
    /// <para>
    /// A service that is already registered is kept, so calling this again only adds <paramref name="configure"/>, and <see cref="IFileRepoCleaner"/> stays
    /// a <see cref="FileRepo"/> that was registered first (see <see cref="AddFileRepo"/>).</para>
    /// </remarks>
    public static IServiceCollection AddFileRepoCleaner(this IServiceCollection services, Action<FileRepoCleaningOptions>? configure = null)
    {
        services.AddOptions<FileRepoCleaningOptions>().ValidateOnStart();

        if (configure is not null)
            services.Configure(configure);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<FileRepoCleaningOptions>, FileRepoCleaningOptionsValidator>());
        services.TryAddSingleton(provider => new FileRepoCleaner(provider.GetRequiredService<IOptions<FileRepoCleaningOptions>>()));
        services.TryAddSingleton<IFileRepoCleaner>(provider => provider.GetRequiredService<FileRepoCleaner>());

        return services;
    }
}
