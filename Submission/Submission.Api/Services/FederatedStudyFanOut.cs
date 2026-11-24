using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;

namespace Submission.Api.Services
{
    /// <summary>Turns a submitted study version into the part each hospital runs.</summary>
    public static class FederatedStudyFanOut
    {
        /// <summary>
        /// Server-owned fields: callers must not be able to inject state, logs or results into a queued task.
        /// </summary>
        public static void ClearServerOwnedFields(TesTask task)
        {
            task.Id = null;
            task.State = TesState.UNKNOWNEnum;
            task.Logs = null;
            task.CreationTime = null;
        }

        /// <summary>
        /// The part of <paramref name="version"/> that hospital <paramref name="site"/> runs: a deep copy of the submitted
        /// task whose <c>fed.sites</c> names only that hospital, queued at the version's submission time. Every other
        /// byte of the task is as submitted. This is the single place that will split the inputs per hospital later.
        /// The caller sets <see cref="FederatedTask.Operation"/> and <see cref="FederatedTask.Dataset"/> from the parsed spec.
        /// </summary>
        public static FederatedTask BuildPart(TesTask submitted, FederatedStudyVersion version, Tre tre, string site)
        {
            var copy = JsonConvert.DeserializeObject<TesTask>(JsonConvert.SerializeObject(submitted))!;
            ClearServerOwnedFields(copy);
            // Parse reads the tag case-insensitively but Tags is case-sensitive: drop every spelling, then set the canonical key.
            foreach (var key in copy.Tags!.Keys.Where(k => k.Equals(FederatedTesProfile.SitesTag, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                copy.Tags.Remove(key);
            }
            copy.Tags[FederatedTesProfile.SitesTag] = site;

            var part = new FederatedTask
            {
                TaskId = Guid.NewGuid(),
                TreId = tre.Id,
                TreName = site,
                StudyId = version.StudyId,
                TesTaskJson = JsonConvert.SerializeObject(copy),
                SubmittedAt = version.SubmittedAt,
                StudyVersion = version
            };
            part.MoveTo(FederatedTaskPhase.Queued, version.SubmittedAt);
            return part;
        }
    }
}
