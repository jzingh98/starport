using Starport.Processor;

if (args.Length == 3 && args[0] == "--remote")
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await RemotePackageProcessor.ProcessAsync(args[1], args[2], timeout.Token);
        return 0;
    }
    catch (Exception error) when (error is not OutOfMemoryException)
    {
        // Signed URLs are capabilities. Neither an HTTP error nor its exception may reach logs.
        Console.Error.WriteLine("Remote package processing failed.");
        return 1;
    }
}
if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: Starport.Processor <archive.zip> <new-output-directory> | --remote <signed-source-get-url> <signed-output-put-url>");
    return 2;
}
try
{
    await GamePackageProcessor.ProcessAsync(args[0], args[1]);
    return 0;
}
catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
