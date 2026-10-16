namespace Dynex.Android;

public static class MobileState
{
    private static MobileSnapshot current = MobileSnapshot.Initial;
    public static MobileSnapshot Current => Volatile.Read(ref current);
    public static event Action? Changed;

    internal static void Publish(MobileSnapshot snapshot)
    {
        Interlocked.Exchange(ref current, snapshot);
        Changed?.Invoke();
    }
}
