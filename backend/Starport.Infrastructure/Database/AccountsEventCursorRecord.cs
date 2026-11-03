namespace Starport.Infrastructure.Database;

/// <summary>Last Accounts event applied to Starport's publication state.</summary>
public sealed class AccountsEventCursorRecord
{
    public required string Id { get; set; }
    public long LastEventId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
