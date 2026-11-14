using FiveSafesTes.Core.Models.Tes;
using Newtonsoft.Json;
using Xunit;

namespace Submission.Api.Tests
{
    /// <summary>The TES 1.1 task states, and the stored values every existing task depends on.</summary>
    public class TesStateTests
    {
        // FederatedTasks.State (and the native submission tables) store the enum's number. Renumbering a state would
        // silently change every stored task, so the numbers are pinned.
        [Theory]
        [InlineData(TesState.UNKNOWNEnum, 1)]
        [InlineData(TesState.QUEUEDEnum, 2)]
        [InlineData(TesState.INITIALIZINGEnum, 3)]
        [InlineData(TesState.RUNNINGEnum, 4)]
        [InlineData(TesState.PAUSEDEnum, 5)]
        [InlineData(TesState.COMPLETEEnum, 6)]
        [InlineData(TesState.EXECUTORERROREnum, 7)]
        [InlineData(TesState.SYSTEMERROREnum, 8)]
        [InlineData(TesState.CANCELEDEnum, 9)]
        [InlineData(TesState.PREEMPTEDEnum, 10)]
        [InlineData(TesState.CANCELINGEnum, 11)]
        public void Each_state_keeps_its_stored_number(TesState state, int stored) =>
            Assert.Equal(stored, (int)state);

        [Fact]
        public void The_model_has_exactly_the_tes_1_1_states()
        {
            var names = Enum.GetValues<TesState>().Select(s => JsonConvert.SerializeObject(s).Trim('"')).ToArray();

            Assert.Equal(new[]
            {
                "UNKNOWN", "QUEUED", "INITIALIZING", "RUNNING", "PAUSED", "COMPLETE",
                "EXECUTOR_ERROR", "SYSTEM_ERROR", "CANCELED", "PREEMPTED", "CANCELING"
            }, names);
        }

        [Theory]
        [InlineData("CANCELING", TesState.CANCELINGEnum)]
        [InlineData("PREEMPTED", TesState.PREEMPTEDEnum)]
        public void A_tes_1_1_state_is_read_from_a_task(string wire, TesState expected) =>
            Assert.Equal(expected, JsonConvert.DeserializeObject<TesTask>($"{{\"state\":\"{wire}\"}}")!.State);
    }
}
