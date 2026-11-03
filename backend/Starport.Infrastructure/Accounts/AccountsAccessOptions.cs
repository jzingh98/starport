namespace Starport.Infrastructure.Accounts;

public sealed class AccountsAccessOptions
{
    public const string Section = "Accounts";
    public string BaseUrl { get; set; } = "";
    public string TokenUrl { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
}

public sealed class AccountsAccessUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
