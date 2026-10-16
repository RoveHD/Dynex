using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace Dynex.Core;

/// <summary>A bounded on-link unicast fallback for access points that suppress multicast/broadcast.</summary>
internal sealed class AutoLanDiscovery
{
    private AutoLanBinding[] _bindings = [];
    private uint[] _positions = [];
    private int _nextBinding;

    internal void Reset(AutoLanBinding[] bindings)
    {
        _bindings = bindings.ToArray();
        _positions = new uint[bindings.Length];
        _nextBinding = 0;
    }

    internal IReadOnlyList<IPAddress> Next(AutoLanBinding[] bindings, int count)
    {
        if (!_bindings.SequenceEqual(bindings)) Reset(bindings);
        var result = new List<IPAddress>(count);
        if (bindings.Length == 0) return result;
        // At most one /16 around each local address, never the entire RFC1918 range.
        // Round robin keeps a second adapter from starving behind a large first subnet.
        var attempts = 0;
        while (result.Count < count && attempts++ < count * bindings.Length * 8)
        {
            var index = _nextBinding++ % bindings.Length;
            if (_nextBinding == int.MaxValue) _nextBinding = 0;
            var binding = bindings[index];
            var local = Number(binding.Address);
            var mask = Number(binding.Mask);
            var hostMask = Math.Min(~mask, 65535u);
            var network = local & ~hostMask;
            // XOR with the local /24 means that /24 is visited first even on /16 or /8.
            var position = _positions[index]++ & hostMask;
            var candidate = network | (position ^ (local & hostMask & 0xffffff00u));
            if (candidate == local || (candidate & ~mask) == 0 || (candidate & ~mask) == ~mask) continue;
            var address = Address(candidate);
            if (!LanAddress.IsAllowed(address, false) || !binding.Contains(address) || result.Contains(address)) continue;
            result.Add(address);
        }
        return result;
    }

    internal static IReadOnlyList<IPAddress> ReadNeighbors(AutoLanBinding[] bindings)
    {
        if (!OperatingSystem.IsWindows() || bindings.Length == 0) return [];
        uint size = 0;
        if (GetIpNetTable(IntPtr.Zero, ref size, false) != 122 || size is < 4 or > 1024 * 1024) return [];
        var table = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetIpNetTable(table, ref size, false) != 0) return [];
            var rows = Marshal.ReadInt32(table);
            if (rows < 0 || rows > (size - 4) / 24) return [];
            var addresses = new List<IPAddress>();
            for (var i = 0; i < rows && addresses.Count < 128; i++)
            {
                var row = IntPtr.Add(table, 4 + i * 24);
                var index = Marshal.ReadInt32(row);
                var type = Marshal.ReadInt32(row, 20);
                if (type is not (3 or 4)) continue; // dynamic or static, never incomplete/invalid
                var bytes = new byte[4];
                Marshal.Copy(IntPtr.Add(row, 16), bytes, 0, 4);
                var address = new IPAddress(bytes);
                if (LanAddress.IsAllowed(address, false) && bindings.Any(b => b.InterfaceIndex == index && b.Contains(address) && !b.Address.Equals(address)))
                    addresses.Add(address);
            }
            return addresses.Distinct().ToArray();
        }
        finally { Marshal.FreeHGlobal(table); }
    }

    private static uint Number(IPAddress ip) => BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
    private static IPAddress Address(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new(bytes);
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIpNetTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order);
}
