using System.Buffers.Binary;
using System.ComponentModel;
using Dynex.Shaper;

internal static unsafe class NdisLifecycleChecks
{
    public static void Run(Action<bool, string> check)
    {
        var original = unchecked((nint)0xFFFFCA1200001234L);
        var replacement = unchecked((nint)0xFFFFCA1200005678L);
        var native = new Transport([original]);
        var device = new NdisPacketDevice(native.Io, original);
        device.Dispose();
        check(device.CleanupError is null && native.Calls.Select(c => c.Index).SequenceEqual([13, 5, 7, 6]),
            "native shutdown verifies owned mode, disables capture, releases event and flushes in order");
        device.Dispose();
        check(native.Calls.Count == 4, "native shutdown is idempotent");

        foreach (var failedIndex in new[] { 13, 5, 7, 6 })
        {
            native = new Transport([replacement]) { FailIndex = failedIndex, FailHandle = original };
            device = new NdisPacketDevice(native.Io, original);
            device.Dispose();
            check(device.CleanupError is null && native.Calls.Last().Index == 1 &&
                  native.Calls.Where(c => c.Index != 1).All(c => c.Handle == original),
                $"removed adapter during IOCTL {failedIndex}: fresh inventory confirms absence without touching replacement");
        }

        native = new Transport([original]) { FailIndex = 13, FailHandle = original };
        device = new NdisPacketDevice(native.Io, original);
        device.Dispose();
        check(device.CleanupError?.Contains("NDISRD IOCTL 13") == true && native.Calls.Count == 2,
            "invalid parameter on a still-listed adapter remains a release failure without retry");

        native = new Transport([replacement]) { FailIndex = 13, FailHandle = original, ErrorCode = 5 };
        device = new NdisPacketDevice(native.Io, original);
        device.Dispose();
        check(device.CleanupError is not null && native.Calls.Count == 1,
            "access denied is never mistaken for an adapter rebind");

        native = new Transport([replacement]) { FailIndex = 13, FailHandle = original, EnumerationFails = true };
        device = new NdisPacketDevice(native.Io, original);
        device.Dispose();
        check(device.CleanupError?.Contains("Adapterwechsel konnte nicht bestätigt werden") == true,
            "failed fresh inventory preserves the original release failure");

        foreach (var returned in new uint[] { 0, 3, 8196, 8840 })
        {
            native = new Transport([replacement]) { FailIndex = 13, FailHandle = original, Returned = returned };
            device = new NdisPacketDevice(native.Io, original);
            device.Dispose();
            check(device.CleanupError is not null, $"truncated or oversized inventory ({returned} bytes) cannot confirm release");
        }

        native = new Transport([]) { FailIndex = 13, FailHandle = original, Returned = 4 };
        device = new NdisPacketDevice(native.Io, original);
        device.Dispose();
        check(device.CleanupError is null, "explicit successful empty inventory confirms that the old adapter is gone");

        native = new Transport([0]) { FailIndex = 13, FailHandle = original };
        device = new NdisPacketDevice(native.Io, original);
        device.Dispose();
        check(device.CleanupError is not null, "null adapter handle is ambiguous rather than evidence of disappearance");

        native = new Transport([original]) { Mode = 4 };
        device = new NdisPacketDevice(native.Io, original);
        device.Dispose();
        check(device.CleanupError?.Contains("Fremder Adaptermodus") == true && native.Calls.Count == 1,
            "foreign adapter mode is never reset, detached or flushed");

        native = new Transport([replacement]) { FailIndex = 13, FailHandle = original };
        device = new NdisPacketDevice(native.Io, original, replacement);
        device.Dispose();
        check(device.CleanupError is null && native.Calls.Skip(2).Select(c => c.Index).SequenceEqual([13, 5, 7, 6]),
            "a removed adapter does not prevent release of another adapter owned by the same session");

        var malformed = new byte[8836];
        BinaryPrimitives.WriteUInt32LittleEndian(malformed, 33);
        try
        {
            NdisPacketDevice.AdapterListContains(malformed, 8836, original);
            throw new Exception("Invalid native adapter count accepted");
        }
        catch (InvalidDataException) { check(true, "oversized native adapter count cannot confirm release"); }
    }

    private sealed class Transport(nint[] currentHandles)
    {
        public int FailIndex = -1, ErrorCode = 87;
        public nint FailHandle;
        public uint Mode = 3;
        public uint Returned = 8836;
        public bool EnumerationFails;
        public List<(int Index, nint Handle)> Calls { get; } = [];

        public uint Io(int index, void* input, int inputLength, void* output, int outputLength)
        {
            var handle = index == 1 ? 0 : *(nint*)input;
            Calls.Add((index, handle));
            if (index == FailIndex && handle == FailHandle)
                throw new Win32Exception(ErrorCode, $"NDISRD IOCTL {index}: injected error {ErrorCode}");
            switch (index)
            {
                case 1:
                    if (EnumerationFails) throw new Win32Exception(5, "injected enumeration failure");
                    Require(inputLength == 8836 && outputLength == 8836 && input == output);
                    var list = new Span<byte>(output, outputLength);
                    list.Clear();
                    BinaryPrimitives.WriteUInt32LittleEndian(list, (uint)currentHandles.Length);
                    for (var i = 0; i < currentHandles.Length; i++)
                        BinaryPrimitives.WriteInt64LittleEndian(list.Slice(8196 + i * 8, 8), (long)currentHandles[i]);
                    return Returned;
                case 13:
                    Require(inputLength == 12 && outputLength == 12 && input == output);
                    *(uint*)((byte*)output + 8) = Mode;
                    return 12;
                case 5:
                    Require(inputLength == 12 && outputLength == 0 && *(uint*)((byte*)input + 8) == 0);
                    return 0;
                case 7:
                    Require(inputLength == 16 && outputLength == 0 && *(nint*)((byte*)input + 8) == 0);
                    return 0;
                case 6:
                    Require(inputLength == 8 && outputLength == 0);
                    return 0;
                default: throw new InvalidOperationException("Unexpected native operation " + index);
            }
        }

        private static void Require(bool valid)
        {
            if (!valid) throw new InvalidOperationException("Native IOCTL ABI or release order mismatch");
        }
    }
}
