using System.Net;
using System.Net.Sockets;

namespace Grayjay.ClientServer.Casting;

public static class CastingAddress
{
    public static IPAddress Select(IPAddress local, IEnumerable<IPAddress> receiverAddresses, Func<IPAddress, IPAddress>? route = null)
    {
        static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4()
            : address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal
                ? new IPAddress(address.GetAddressBytes()) : address;
        local = Normalize(local);
        if (!local.IsIPv6LinkLocal) return local;
        route ??= receiver => {
            using var socket = new Socket(receiver.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect(new IPEndPoint(receiver, 9));
            return ((IPEndPoint)socket.LocalEndPoint!).Address;
        };
        foreach (var receiver in receiverAddresses.Select(Normalize)
                     .Where(address => !address.IsIPv6LinkLocal)
                     .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            try
            {
                var address = Normalize(route(receiver));
                if (!address.IsIPv6LinkLocal && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                    return address;
            }
            catch (SocketException) { }
        }
        throw new InvalidOperationException("No usable address for casting media. Connect the receiver using IPv4 or a non-link-local IPv6 address.");
    }
}
