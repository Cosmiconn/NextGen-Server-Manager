namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// Visibility identity for one authenticated Zone connection. Feed packets in
/// receive order and dispose when that connection ends. A binding establishes
/// only an unchanged appearance in this session, never attackability or range.
/// </summary>
internal sealed class FiestaMobVisibilitySession : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<ushort, FiestaMobTargetBinding> visible = new();
    private bool closed;

    internal FiestaMobVisibilitySession(FiestaZoneActorIdentity actor) => Actor = actor;
    internal FiestaZoneActorIdentity Actor { get; }

    internal void Observe(int header, int type, ReadOnlySpan<byte> body)
    {
        // Parse complete frames before changing any entry. A malformed batch
        // must not leave a partially updated registry usable for targeting.
        IReadOnlyList<FiestaVisibleMob>? appearances;
        ushort? removed;
        FiestaObservedMovement? movement;
        try
        {
            appearances = FiestaMobVisibilityProtocol.ReadAppearances(header, type, body);
            removed = FiestaMobVisibilityProtocol.ReadRemovedHandle(header, type, body);
            movement = FiestaMovementProtocol.ReadBroadcast(header, type, body);
        }
        catch (InvalidDataException)
        {
            // After a malformed visibility frame the current view is incomplete.
            // Require a new session rather than retaining potentially stale targets.
            Dispose();
            throw;
        }
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (appearances is not null)
                foreach (var mob in appearances)
                {
                    // A handle may be recycled even with the same template and
                    // coordinates. Every appearance creates a distinct binding.
                    visible[mob.Handle] = new FiestaMobTargetBinding(mob);
                }
            if (removed is not null) visible.Remove(removed.Value);
            // An observed walk/run invalidates the last appearance coordinates.
            // A later appearance can make it a candidate again, but neither
            // packet proves that it is stationary or currently within range.
            if (movement is not null) visible.Remove(movement.Handle);
        }
    }

    internal FiestaMobTargetBinding? Bind(ushort handle)
    {
        lock (gate)
            return !closed && visible.TryGetValue(handle, out var binding) ? binding : null;
    }

    internal bool IsCurrent(FiestaMobTargetBinding binding)
    {
        lock (gate)
            return !closed && visible.TryGetValue(binding.Mob.Handle, out var current)
                && ReferenceEquals(current, binding);
    }

    internal FiestaMobVisibilityObservation Snapshot()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            return new FiestaMobVisibilityObservation(Actor, DateTimeOffset.UtcNow,
                visible.Values.Select(x => x.Mob).OrderBy(x => x.Handle).ToArray());
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            closed = true;
            visible.Clear();
        }
    }

    internal static void RunSelfTest()
    {
        // Captured identity/coordinates; unrelated fields are synthetic zeros.
        var appearance = new byte[149];
        Convert.FromHexString("6623021240143000000A28000028").CopyTo(appearance, 0);
        var actor = new FiestaZoneActorIdentity(12000, 11662, 10817);
        using var session = new FiestaMobVisibilitySession(actor);
        if (session.Bind(9062) is not null) throw new InvalidDataException("Unseen target was available.");
        session.Observe(7, 8, appearance);
        var first = session.Bind(9062) ?? throw new InvalidDataException("Current mob was not bound.");
        if (!session.IsCurrent(first)) throw new InvalidDataException("Fresh binding was not current.");

        // A second connection can receive an identical handle/template but must
        // never accept the binding from the earlier connection.
        using var other = new FiestaMobVisibilitySession(actor);
        other.Observe(7, 8, appearance);
        if (other.IsCurrent(first)) throw new InvalidDataException("Binding crossed Zone sessions.");
        session.Observe(7, 14, Convert.FromHexString("6623"));
        if (session.IsCurrent(first) || session.Bind(9062) is not null)
            throw new InvalidDataException("Removed target remained current.");
        session.Observe(7, 8, appearance);
        var recycled = session.Bind(9062)!;
        if (session.IsCurrent(first) || !session.IsCurrent(recycled))
            throw new InvalidDataException("Recycled handle retained an old binding.");
        session.Observe(7, 8, appearance);
        if (session.IsCurrent(recycled))
            throw new InvalidDataException("Repeated appearance retained an old binding.");

        var stationary = session.Bind(9062)!;
        session.Observe(8, 24, Convert.FromHexString("6623143000000A28000037300000102800001D006E20"));
        if (session.IsCurrent(stationary) || session.Bind(9062) is not null)
            throw new InvalidDataException("Moving target retained stale appearance coordinates.");
        session.Observe(7, 8, appearance);
        var beforeMalformed = session.Bind(9062)!;
        var malformedRejected = false;
        try { session.Observe(7, 9, new byte[] { 1 }); }
        catch (InvalidDataException) { malformedRejected = true; }
        if (!malformedRejected || session.IsCurrent(beforeMalformed) || session.Bind(9062) is not null)
            throw new InvalidDataException("Malformed visibility did not invalidate session bindings.");

        var last = other.Bind(9062)!;
        other.Dispose();
        if (other.IsCurrent(last) || other.Bind(9062) is not null)
            throw new InvalidDataException("Closed connection retained target bindings.");
        var closedRejected = false;
        try { other.Observe(7, 8, appearance); }
        catch (ObjectDisposedException) { closedRejected = true; }
        if (!closedRejected) throw new InvalidDataException("Closed visibility session was revived.");
    }
}

internal sealed class FiestaMobTargetBinding
{
    internal FiestaMobTargetBinding(FiestaVisibleMob mob) => Mob = mob;
    internal FiestaVisibleMob Mob { get; }
}

/// <summary>Historical observation at hold end, not a reusable targeting capability.</summary>
public sealed record FiestaMobVisibilityObservation(FiestaZoneActorIdentity Actor,
    DateTimeOffset ObservedUtc, IReadOnlyList<FiestaVisibleMob> AppearanceCandidates);
