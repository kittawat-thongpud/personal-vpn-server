using System;
using System.Threading;

namespace PersonalVpn;

// Backend serializes access with its state lock so proof and state commit use the same epoch.
internal sealed class RecoveryNetwork : IDisposable
{
    private CancellationTokenSource changed = new();

    internal long Generation { get; private set; }
    internal string? Signature { get; private set; }

    internal void Reset(string? signature)
    {
        Signature = signature;
        Advance();
    }

    internal bool Observe(string? signature)
    {
        if (signature == null || signature == Signature) return false;
        Signature = signature;
        Advance();
        return true;
    }

    internal bool IsCurrent(long expected) => Generation == expected;

    internal (long Generation, CancellationTokenSource Work) BeginWork(CancellationToken session) =>
        (Generation, CancellationTokenSource.CreateLinkedTokenSource(session, changed.Token));

    private void Advance()
    {
        var previous = changed;
        changed = new CancellationTokenSource();
        Generation++;
        previous.Cancel();
        previous.Dispose();
    }

    public void Dispose() => changed.Dispose();
}
