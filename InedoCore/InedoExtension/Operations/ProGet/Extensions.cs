﻿using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Inedo.Extensions.PackageSources;
using Inedo.Extensions.UniversalPackages;
using Inedo.UPack.Packaging;

#nullable enable

namespace Inedo.Extensions.Operations.ProGet
{
    internal static class Extensions
    {
        public static async Task EnsureProGetConnectionInfoAsync(this IFeedConfiguration config, ICredentialResolutionContext context, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(config.PackageSourceName))
            {
                var source = await AhPackages.GetPackageSourceAsync(new PackageSourceId(config.PackageSourceName), context, cancellationToken);
                if (source == null)
                    throw new ExecutionFailureException($"Package source {config.PackageSourceName} was not found.");

                switch (source.SourceId.Format)
                {
                    case PackageSourceIdFormat.SecureResource:
                        if (!context.TryGetSecureResource(source.SourceId.GetResourceName(), out var secureResource))
                            throw new ExecutionFailureException($"Secure resource {source.SourceId.GetResourceName()} was not found.");

                        var feedUrl = secureResource switch
                        {
                            UniversalPackageSource ups => ups.ApiEndpointUrl,
                            NuGetPackageSource nps => nps.ApiEndpointUrl,
                            _ => throw new ExecutionFailureException($"Secure resource {source.SourceId.GetResourceName()} was not a supported type.")
                        };

                        if (!TryParseFeedUrl(feedUrl, out var serviceUrl, out _, out var feedName))
                            throw new ExecutionFailureException($"Secure resource {source.SourceId.GetResourceName()} does not refer to a valid ProGet feed URL.");

                        if (string.IsNullOrEmpty(config.ApiUrl))
                            config.ApiUrl = serviceUrl;
                        if (string.IsNullOrEmpty(config.FeedName))
                            config.FeedName = feedName;

                        switch (secureResource.GetCredentials(context))
                        {
                            case UsernamePasswordCredentials upc:
                                config.UserName = upc.UserName;
                                config.Password = AH.Unprotect(upc.Password);
                                break;

                            case TokenCredentials tc:
                                config.ApiKey = AH.Unprotect(tc.Token);
                                break;
                        }

                        break;

                    case PackageSourceIdFormat.ProGetFeed:
                        var creds = SecureCredentials.TryCreate(source.SourceId.GetProGetServiceCredentialName(), context);
                        if (creds == null)
                            throw new ExecutionFailureException($"ProGet service credentials {source.SourceId.GetProGetServiceCredentialName()} not found.");
                        if (creds is not ProGetServiceCredentials svcCreds)
                            throw new ExecutionFailureException($"{source.SourceId.GetProGetServiceCredentialName()} is not a ProGet service credential.");

                        if (string.IsNullOrEmpty(config.ApiUrl))
                            config.ApiUrl = svcCreds.ServiceUrl;
                        if (string.IsNullOrEmpty(config.ApiKey))
                            config.ApiKey = svcCreds.APIKey;
                        if (string.IsNullOrEmpty(config.UserName))
                            config.UserName = svcCreds.UserName;
                        if (string.IsNullOrEmpty(config.Password))
                            config.Password = svcCreds.Password;

                        if (string.IsNullOrEmpty(config.FeedName))
                            config.FeedName = source.SourceId.GetFeedName();

                        break;

                    case PackageSourceIdFormat.Url:
                        if (string.IsNullOrEmpty(config.ApiUrl))
                            config.ApiUrl = source.SourceId.GetUrl();
                        break;

                    default:
                        throw new NotSupportedException();
                }
            }

            if (string.IsNullOrEmpty(config.ApiUrl) || string.IsNullOrEmpty(config.FeedName))
            {
                if (!TryParseFeedUrl(config.FeedUrl, out var serviceUrl, out _, out var feedName))
                    throw new ExecutionFailureException("ServiceUrl and FeedName are required.");

                config.ApiUrl = serviceUrl;
                config.FeedName = feedName;
            }
        }

        public static bool TryParseFeedUrl(string? feedUrl, [NotNullWhen(true)] out string? serviceUrl, [NotNullWhen(true)] out string? feedType, [NotNullWhen(true)] out string? feedName)
        {
            var m = Regex.Match(feedUrl ?? string.Empty, @"^(?<1>(https?://)?[^/]+)/(?<2>upack|nuget)(/?(?<3>[^/]+)/?)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);
            if (!m.Success)
            {
                serviceUrl = null;
                feedType = null;
                feedName = null;
                return false;
            }

            serviceUrl = m.Groups[1].Value;
            feedType = m.Groups[2].Value;
            feedName = Uri.UnescapeDataString(m.Groups[3].Value);
            return true;
        }

        public static async Task ResolveAttachedPackageAsync(this IFeedPackageConfiguration config, IOperationExecutionContext context)
        {
            if (config.PackageVersion == "attached")
            {
                if (SDK.ProductName != "BuildMaster")
                    throw new ExecutionFailureException("Setting \"attached\" for the package version is only supported in BuildMaster.");

                context.Log.LogWarning("The value \"attached\" for package version is deprecated, and will be remove din a future version.");
                context.Log.LogDebug("Searching for attached package version...");

                var packageManager = await context.TryGetServiceAsync<IPackageManager>();
                if (packageManager == null)
                    throw new ExecutionFailureException("Package manager is not available.");

                var match = (await packageManager.GetBuildPackagesAsync(context.CancellationToken))
                    .FirstOrDefault(p => p.Active
                        && string.Equals(p.Name, config.PackageName, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(p.PackageSource, config.PackageSourceName, StringComparison.OrdinalIgnoreCase));

                if (match == null)
                    throw new ExecutionFailureException($"The current build has no active attached packages named {config.PackageName} from source {config.PackageSourceName}.");

                context.Log.LogInformation($"Package version from attached package {config.PackageName} (source {config.PackageSourceName}): {match.Version}");
                config.PackageVersion = match.Version;
            }
        }

        public static async Task InstallPackageAsync(this IFeedPackageInstallationConfiguration config, ILogSink log, string targetDirectory, CancellationToken cancellationToken)
        {
            var client = new ProGetFeedClient(config, log);
            using var downloadStream = await client.GetPackageStreamAsync(config.PackageName!, config.PackageVersion!, cancellationToken);
            log.LogInformation("Downloading package...");
            using var tempStream = new TemporaryStream();
            await downloadStream.CopyToAsync(tempStream, cancellationToken);

            log.LogDebug($"Package downloaded ({tempStream.Length} bytes.");

            log.LogInformation($"Installing package to {targetDirectory}...");
            tempStream.Position = 0;
            using var package = new UniversalPackage(tempStream);
            await package.ExtractContentItemsAsync(targetDirectory, cancellationToken);
            log.LogInformation("Package installed.");

            if (config.LocalRegistry != LocalRegistryOptions.None)
            {
                var registeredPackage = new RegisteredPackage
                {
                    Group = package.Group,
                    Name = package.Name,
                    Version = package.Version!.ToString(),
                    InstallPath = targetDirectory,
                    FeedUrl = $"{config.ApiUrl!.TrimEnd('/')}/upack/{Uri.EscapeDataString(config.FeedName ?? string.Empty)}",
                    InstallationDate = DateTimeOffset.Now.ToString("o"),
                    InstalledUsing = $"{SDK.ProductName}/{SDK.ProductVersion} (InedoCore/{Extension.Version})"
                };

                log.LogDebug("Recording installation in package registry...");
                using var registry = PackageRegistry.GetRegistry(config.LocalRegistry == LocalRegistryOptions.User);
                await registry.LockAsync(cancellationToken);
                await registry.RegisterPackageAsync(registeredPackage, cancellationToken).ConfigureAwait(false);
            }

            log.LogInformation("Package installation complete.");
        }
    }
}
