namespace Starport.Api.Storage;

/// <summary>Filesystem layout for local development; hosted storage uses object capabilities.</summary>
public sealed class StoragePaths(IConfiguration config, IHostEnvironment environment)
{
    public string Root { get; } = ResolveRoot(config, environment);

    public string Upload(Guid id) => Path.Combine(Root, "uploads", id.ToString("N") + ".zip");
    public string Staging(Guid id) => Path.Combine(Root, "staging", id.ToString("N"));
    public string Release(Guid id) => Path.Combine(Root, "releases", id.ToString("N"));

    private static string ResolveRoot(IConfiguration config, IHostEnvironment environment)
    {
        var value = config["Storage:Root"];
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException("Storage:Root is required for filesystem storage.");
        return Path.GetFullPath(Path.IsPathRooted(value) ? value :
            Path.Combine(environment.ContentRootPath, value));
    }
}
