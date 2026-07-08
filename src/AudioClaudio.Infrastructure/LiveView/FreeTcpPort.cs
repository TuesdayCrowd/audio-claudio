using System.Net;
using System.Net.Sockets;

namespace AudioClaudio.Infrastructure.LiveView;

/// <summary>
/// Picks a free TCP port by binding an ephemeral probe listener to loopback:0, reading back
/// whatever port the OS assigned, then releasing it immediately so <see cref="LiveNotationServer"/>
/// can bind an <c>HttpListener</c> to the same number. A microscopic release-then-rebind race is
/// possible in principle (see the live-notation design doc's "Port selection" decision);
/// accepted as a documented risk for this single-user, localhost-only, once-per-session feature.
/// </summary>
internal static class FreeTcpPort
{
    internal static int Find()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
