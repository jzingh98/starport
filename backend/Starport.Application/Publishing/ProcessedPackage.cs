namespace Starport.Application.Publishing;

public sealed record ProcessedFile(string Path, long Bytes, string Sha256);
public sealed record ProcessedPackage(string EntryPoint, string ArchiveSha256,
    IReadOnlyList<ProcessedFile> Files, long TotalBytes);
