namespace NextGen.Fiesta.ServerManager.Services;

/// <summary>
/// An opt-in, single-leg pilot. The caller must establish that the complete
/// corridor is traversable on the current map before enabling this plan.
/// </summary>
public sealed record FiestaMovementPilotPlan(uint StartX, uint StartY, uint EndX, uint EndY)
{
    public TimeSpan StartDelay { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan TravelDuration { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan SettleDuration { get; init; } = TimeSpan.FromSeconds(3);

    public void Validate(TimeSpan holdDuration)
    {
        var dx = (double)EndX - StartX;
        var dy = (double)EndY - StartY;
        if (dx * dx + dy * dy is <= 0 or > 100)
            throw new ArgumentException("Movement pilot requires a nonzero leg of at most 10 map units.");
        if (StartDelay < TimeSpan.Zero || StartDelay > TimeSpan.FromMinutes(1)
            || TravelDuration <= TimeSpan.Zero || TravelDuration > TimeSpan.FromMinutes(1)
            || SettleDuration <= TimeSpan.Zero || SettleDuration > TimeSpan.FromMinutes(1))
            throw new ArgumentException("Invalid movement pilot timing.");
        if (holdDuration <= StartDelay + TravelDuration + SettleDuration)
            throw new ArgumentException("Session hold must exceed the complete movement pilot interval.");
    }

    internal void VerifyActor(FiestaZoneActorIdentity? actor)
    {
        if (actor is null || actor.LoginX != StartX || actor.LoginY != StartY)
            throw new InvalidOperationException("Current Zone login position does not match the movement pilot start.");
    }
}

/// <summary>Transmission evidence only; requires a separate server position check.</summary>
public sealed record FiestaMovementPilotCommands(
    ushort ActorHandle, uint StartX, uint StartY, uint EndX, uint EndY,
    DateTimeOffset RunSentUtc, DateTimeOffset StopSentUtc)
{
    public bool ServerPositionVerified => false;
}

internal static class FiestaMovementPilot
{
    internal static async Task<FiestaMovementPilotCommands> SendAsync(
        FiestaWireConnection zone, FiestaZoneActorIdentity actor,
        FiestaMovementPilotPlan plan, CancellationToken token)
    {
        plan.VerifyActor(actor);
        await Task.Delay(plan.StartDelay, token);
        await zone.SendDecryptedPayloadAsync(
            FiestaMovementProtocol.Run(plan.StartX, plan.StartY, plan.EndX, plan.EndY), token);
        var runSent = DateTimeOffset.UtcNow;
        await Task.Delay(plan.TravelDuration, token);
        await zone.SendDecryptedPayloadAsync(FiestaMovementProtocol.Stop(plan.EndX, plan.EndY), token);
        var stopSent = DateTimeOffset.UtcNow;
        await Task.Delay(plan.SettleDuration, token);
        return new FiestaMovementPilotCommands(actor.Handle, plan.StartX, plan.StartY,
            plan.EndX, plan.EndY, runSent, stopSent);
    }
}
