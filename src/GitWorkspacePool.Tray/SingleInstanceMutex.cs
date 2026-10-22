namespace GitWorkspacePool.Tray;

// The production tray and isolated process E2E must use the same OS mutex gate.
internal static class SingleInstanceMutex
{
    public static Mutex Acquire(string name, out bool firstInstance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new Mutex(initiallyOwned: true, name: name, createdNew: out firstInstance);
    }
}
