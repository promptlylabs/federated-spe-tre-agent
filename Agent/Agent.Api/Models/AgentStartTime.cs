namespace Agent.Api.Models
{
    /// <summary>
    /// When this Agent process started. Settings are read only at startup, so a configuration change always comes with
    /// a restart; the give-up window for an unreachable dataset bucket counts from the later of this and enqueue.
    /// </summary>
    public sealed record AgentStartTime(DateTimeOffset Value);
}
