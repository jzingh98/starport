using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Starport.Api.Startup;

/// <summary>Only listed proxies may supply the client address and scheme.</summary>
public sealed class ForwardedHeadersSettings
{
    public List<string>? KnownNetworks { get; set; }
    public int ForwardLimit { get; set; } = 1;

    public bool IsValid() => ForwardLimit >= 1 &&
        (KnownNetworks ?? []).All(value => TryParse(value, out _));

    public void Apply(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = ForwardLimit;
        foreach (var value in KnownNetworks ?? [])
            if (TryParse(value, out var network))
                options.KnownIPNetworks.Add(network);
    }

    private static bool TryParse(string? value, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        if (value.Contains('/'))
            return IPNetwork.TryParse(value, out network);
        if (!IPAddress.TryParse(value, out var address))
            return false;
        network = new IPNetwork(address,
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
        return true;
    }
}
