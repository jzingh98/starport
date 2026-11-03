namespace Starport.Application.Publishing;

public static class PackagePath
{
    public static bool IsSafe(string path, bool directory = false)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith('/') || path.Contains('\\') ||
            path.Contains(':') || path.Any(char.IsControl))
            return false;
        if (directory && path.EndsWith('/')) path = path[..^1];
        if (path.EndsWith('/')) return false;
        return path.Split('/').All(segment => segment is not ("" or "." or ".."));
    }
}
