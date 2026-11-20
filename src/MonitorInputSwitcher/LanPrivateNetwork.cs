using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MonitorInputSwitcher;

/// <summary>
/// One operational IPv4 unicast address on a Windows Private-profile adapter together
/// with its subnet mask. The route only answers addressing questions: it opens no
/// sockets, sends no traffic and changes no network state.
/// </summary>
public sealed record LanPrivateRoute(IPAddress LocalAddress, IPAddress SubnetMask)
{
    private const int MinimumPrefixLength = 1;
    private const int MaximumPrefixLength = 30;

    /// <summary>
    /// The directed broadcast address of the subnet. A route that was not built from a
    /// usable unicast address and contiguous subnet mask reports <see cref="IPAddress.Any"/>
    /// so an unusable route never exposes a plausible link address.
    /// </summary>
    public IPAddress BroadcastAddress
    {
        get
        {
            return TryGetSubnet(out _, out _, out uint broadcast)
                ? FromUInt32(broadcast)
                : IPAddress.Any;
        }
    }

    /// <summary>
    /// True only when <paramref name="address"/> is an ordinary unicast host inside this
    /// route's subnet and is neither this host, the subnet address nor the directed
    /// broadcast address.
    /// </summary>
    public bool ContainsPeer(IPAddress address)
    {
        if (address is null)
        {
            return false;
        }

        if (!IsEligibleHostAddress(address))
        {
            return false;
        }

        if (!TryGetSubnet(out uint mask, out uint network, out uint broadcast))
        {
            return false;
        }

        uint local = ToUInt32(LocalAddress.GetAddressBytes());
        uint peer = ToUInt32(address.GetAddressBytes());

        if (peer == local)
        {
            return false;
        }

        if ((peer & mask) != network)
        {
            return false;
        }

        return peer != network && peer != broadcast;
    }

    /// <summary>
    /// Builds a route only from an eligible IPv4 unicast address and a contiguous subnet
    /// mask that leaves room for ordinary hosts. Everything else is rejected rather than
    /// guessed at.
    /// </summary>
    internal static bool TryCreate(
        IPAddress? localAddress,
        IPAddress? subnetMask,
        out LanPrivateRoute? route)
    {
        route = null;
        if (localAddress is null || subnetMask is null)
        {
            return false;
        }

        var candidate = new LanPrivateRoute(localAddress, subnetMask);
        if (!candidate.TryGetSubnet(out _, out _, out _))
        {
            return false;
        }

        route = candidate;
        return true;
    }

    private bool TryGetSubnet(out uint mask, out uint network, out uint broadcast)
    {
        mask = 0;
        network = 0;
        broadcast = 0;

        if (!IsEligibleHostAddress(LocalAddress))
        {
            return false;
        }

        if (!TryGetMask(SubnetMask, out mask, out int prefixLength))
        {
            return false;
        }

        if (prefixLength < MinimumPrefixLength || prefixLength > MaximumPrefixLength)
        {
            return false;
        }

        uint local = ToUInt32(LocalAddress.GetAddressBytes());
        network = local & mask;
        broadcast = network | ~mask;
        return local != network && local != broadcast;
    }

    private static bool TryGetMask(IPAddress? subnetMask, out uint mask, out int prefixLength)
    {
        mask = 0;
        prefixLength = 0;

        if (subnetMask is null || subnetMask.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] maskBytes = subnetMask.GetAddressBytes();
        if (maskBytes.Length != sizeof(uint))
        {
            return false;
        }

        uint value = ToUInt32(maskBytes);
        uint remaining = value;
        int ones = 0;
        while ((remaining & 0x80000000u) != 0)
        {
            ones++;
            remaining <<= 1;
        }

        // A usable subnet mask is a run of leading one bits followed only by zero bits;
        // 0.0.0.0 is not a subnet.
        if (remaining != 0 || ones == 0)
        {
            return false;
        }

        mask = value;
        prefixLength = ones;
        return true;
    }

    /// <summary>
    /// Rejects addresses that can never identify an ordinary LAN peer: IPv6, the "any"
    /// and "this network" blocks, loopback, multicast/reserved, the limited broadcast
    /// address and the APIPA link-local block.
    /// </summary>
    private static bool IsEligibleHostAddress(IPAddress? address)
    {
        if (address is null || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != sizeof(uint))
        {
            return false;
        }

        if (bytes[0] == 0 || bytes[0] == 127 || bytes[0] >= 224)
        {
            return false;
        }

        return bytes[0] != 169 || bytes[1] != 254;
    }

    private static uint ToUInt32(byte[] bytes)
    {
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private static IPAddress FromUInt32(uint value)
    {
        return new IPAddress(new[]
        {
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        });
    }
}

/// <summary>
/// Selects the operational IPv4 unicast routes of adapters that Windows' Network List
/// Manager (NLM) reports as connected with a Private category and no connected
/// non-Private category on the same adapter. The selection fails closed: any COM or
/// profile error, invalid mask, unknown category or unexpected COM reply yields no
/// routes. No sockets, discovery traffic, registry reads or network changes occur here.
/// </summary>
public static class LanPrivateNetwork
{
    private const string NetworkListManagerClsid = "DCB00C01-570F-4A9B-8D69-199FDBA5723B";

    // IEnumNetworkConnections::Next returns S_FALSE once the enumerator is exhausted.
    private const int SFalse = 1;

    public static IReadOnlyList<LanPrivateRoute> GetRoutes()
    {
        try
        {
            Dictionary<Guid, AdapterProfile>? profiles = TryReadAdapterProfiles();
            if (profiles is null || profiles.Count == 0)
            {
                return Array.Empty<LanPrivateRoute>();
            }

            var routes = new List<LanPrivateRoute>();
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (!Guid.TryParse(adapter.Id, out Guid adapterId))
                {
                    continue;
                }

                if (!profiles.TryGetValue(adapterId, out AdapterProfile profile) ||
                    !profile.IsPrivateOnly)
                {
                    continue;
                }

                IPInterfaceProperties properties;
                try
                {
                    properties = adapter.GetIPProperties();
                }
                catch (Exception exception)
                    when (exception is NetworkInformationException or PlatformNotSupportedException)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation unicast in properties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (LanPrivateRoute.TryCreate(
                            unicast.Address,
                            unicast.IPv4Mask,
                            out LanPrivateRoute? route))
                    {
                        routes.Add(route!);
                    }
                }
            }

            return routes.Count == 0 ? Array.Empty<LanPrivateRoute>() : routes;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Array.Empty<LanPrivateRoute>();
        }
    }

    /// <summary>
    /// Reads the connected NLM connections and records, per adapter GUID, whether a
    /// Private category and any non-Private category were observed. Returns null when
    /// the profile data cannot be trusted, which the caller treats as no routes.
    /// </summary>
    private static Dictionary<Guid, AdapterProfile>? TryReadAdapterProfiles()
    {
        var profiles = new Dictionary<Guid, AdapterProfile>();
        INetworkListManager? manager = null;
        IEnumNetworkConnections? connections = null;

        try
        {
            manager = (INetworkListManager)(object)new NetworkListManagerClass();

            int hr = manager.GetNetworkConnections(out connections);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (connections is null)
            {
                return null;
            }

            while (true)
            {
                INetworkConnection? connection = null;
                try
                {
                    // Only one element is ever requested; S_FALSE with zero fetched is the
                    // documented end of the enumerator.
                    hr = connections.Next(1, out connection, out uint fetched);
                    if (hr < 0)
                    {
                        Marshal.ThrowExceptionForHR(hr);
                    }

                    if (hr != 0 && hr != SFalse)
                    {
                        return null;
                    }

                    if (fetched == 0)
                    {
                        break;
                    }

                    if (connection is null)
                    {
                        return null;
                    }

                    if (!TryReadConnection(connection, profiles))
                    {
                        return null;
                    }
                }
                finally
                {
                    ReleaseComObject(connection);
                }
            }

            return profiles;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
        finally
        {
            ReleaseComObject(connections);
            ReleaseComObject(manager);
        }
    }

    private static bool TryReadConnection(
        INetworkConnection connection,
        Dictionary<Guid, AdapterProfile> profiles)
    {
        INetwork? network = null;

        try
        {
            int hr = connection.get_IsConnected(out bool connected);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (!connected)
            {
                return true;
            }

            hr = connection.GetNetwork(out network);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (network is null)
            {
                return false;
            }

            hr = network.GetCategory(out NlmNetworkCategory category);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            hr = connection.GetAdapterId(out Guid adapterId);
            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            if (adapterId == Guid.Empty)
            {
                return true;
            }

            profiles.TryGetValue(adapterId, out AdapterProfile profile);
            switch (category)
            {
                case NlmNetworkCategory.Private:
                    profile.HasPrivate = true;
                    break;
                case NlmNetworkCategory.Public:
                case NlmNetworkCategory.Domain:
                    profile.HasNonPrivate = true;
                    break;
                default:
                    // An unknown category is never trusted as Private.
                    profile.HasNonPrivate = true;
                    break;
            }

            profiles[adapterId] = profile;
            return true;
        }
        finally
        {
            ReleaseComObject(network);
        }
    }

    private static void ReleaseComObject(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failed release must not turn an untrusted profile into a usable route.
        }
    }

    private struct AdapterProfile
    {
        public bool HasPrivate;
        public bool HasNonPrivate;

        public readonly bool IsPrivateOnly => HasPrivate && !HasNonPrivate;
    }

    private enum NlmNetworkCategory
    {
        Public = 0,
        Private = 1,
        Domain = 2
    }

    [ComImport]
    [Guid(NetworkListManagerClsid)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class NetworkListManagerClass
    {
    }

    [ComImport]
    [Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        // The declaration order below is the native vtable order after IDispatch.
        // Only GetNetworkConnections is invoked; the rest exists to keep the slot layout.
        [PreserveSig]
        [DispId(1)]
        int GetNetworks(int flags, [MarshalAs(UnmanagedType.Interface)] out object networks);

        [PreserveSig]
        [DispId(2)]
        int GetNetwork(Guid networkId, [MarshalAs(UnmanagedType.Interface)] out INetwork network);

        [PreserveSig]
        [DispId(3)]
        int GetNetworkConnections(
            [MarshalAs(UnmanagedType.Interface)] out IEnumNetworkConnections connections);

        [PreserveSig]
        [DispId(4)]
        int GetNetworkConnection(
            Guid connectionId,
            [MarshalAs(UnmanagedType.Interface)] out INetworkConnection connection);

        [PreserveSig]
        [DispId(5)]
        int IsConnectedToInternet([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        [DispId(6)]
        int IsConnected([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        [DispId(7)]
        int GetConnectivity(out int connectivity);
    }

    [ComImport]
    [Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        // Only GetCategory is invoked; the preceding slots preserve the native order.
        [PreserveSig]
        [DispId(1)]
        int get_Name([MarshalAs(UnmanagedType.BStr)] out string name);

        [PreserveSig]
        [DispId(1)]
        int put_Name([MarshalAs(UnmanagedType.BStr)] string name);

        [PreserveSig]
        [DispId(2)]
        int get_Description([MarshalAs(UnmanagedType.BStr)] out string description);

        [PreserveSig]
        [DispId(2)]
        int put_Description([MarshalAs(UnmanagedType.BStr)] string description);

        [PreserveSig]
        [DispId(3)]
        int get_NetworkId(out Guid networkId);

        [PreserveSig]
        [DispId(4)]
        int get_DomainType(out int domainType);

        [PreserveSig]
        [DispId(5)]
        int get_NetworkConnections(
            [MarshalAs(UnmanagedType.Interface)] out IEnumNetworkConnections connections);

        [PreserveSig]
        [DispId(6)]
        int GetTimeCreatedAndConnected(
            out uint lowDateTimeCreated,
            out uint highDateTimeCreated,
            out uint lowDateTimeConnected,
            out uint highDateTimeConnected);

        [PreserveSig]
        [DispId(7)]
        int get_IsConnectedToInternet(
            [MarshalAs(UnmanagedType.VariantBool)] out bool connectedToInternet);

        [PreserveSig]
        [DispId(8)]
        int get_IsConnected([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        [DispId(9)]
        int GetConnectivity(out int connectivity);

        [PreserveSig]
        [DispId(10)]
        int GetCategory(out NlmNetworkCategory category);
    }

    [ComImport]
    [Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        [PreserveSig]
        [DispId(1)]
        int GetNetwork([MarshalAs(UnmanagedType.Interface)] out INetwork network);

        [PreserveSig]
        [DispId(2)]
        int get_IsConnectedToInternet(
            [MarshalAs(UnmanagedType.VariantBool)] out bool connectedToInternet);

        [PreserveSig]
        [DispId(3)]
        int get_IsConnected([MarshalAs(UnmanagedType.VariantBool)] out bool connected);

        [PreserveSig]
        [DispId(4)]
        int GetConnectivity(out int connectivity);

        [PreserveSig]
        [DispId(5)]
        int GetConnectionId(out Guid connectionId);

        [PreserveSig]
        [DispId(6)]
        int GetAdapterId(out Guid adapterId);
    }

    [ComImport]
    [Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [PreserveSig]
        [DispId(1)]
        int get__NewEnum([MarshalAs(UnmanagedType.Interface)] out object newEnum);

        [PreserveSig]
        [DispId(2)]
        int Next(
            uint celt,
            [MarshalAs(UnmanagedType.Interface)] out INetworkConnection? connection,
            out uint fetched);

        [PreserveSig]
        [DispId(3)]
        int Skip(uint celt);

        [PreserveSig]
        [DispId(4)]
        int Reset();

        [PreserveSig]
        [DispId(5)]
        int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumNetworkConnections clone);
    }
}
