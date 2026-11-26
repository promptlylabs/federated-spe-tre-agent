using System.Collections.Concurrent;

namespace Agent.Api.Models
{
    /// <summary>
    /// When each task first hit a Docker fault that may pass by itself, so its retry window counts from that fault and
    /// not from when the task was queued: a task that waited long in the queue still gets the whole window. Kept in
    /// memory, so an Agent restart gives each task the whole window again, as it does for an unreachable dataset bucket.
    /// </summary>
    public sealed class DockerFaultTimes
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _first = new();

        /// <summary>When the task's current run of Docker faults began; <paramref name="now"/> if this is the first.</summary>
        public DateTimeOffset FirstSeen(string taskId, DateTimeOffset now) => _first.GetOrAdd(taskId, now);

        /// <summary>Forgets the task's faults: Docker answered again, or the task was given up.</summary>
        public void Clear(string taskId) => _first.TryRemove(taskId, out _);
    }
}
