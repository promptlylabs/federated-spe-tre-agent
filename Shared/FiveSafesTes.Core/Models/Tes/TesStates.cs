using System.Reflection;
using System.Runtime.Serialization;

namespace FiveSafesTes.Core.Models.Tes
{
    /// <summary>TES state names on the wire ("RUNNING") and the enum (<see cref="TesState.RUNNINGEnum"/>).</summary>
    public static class TesStates
    {
        private static readonly Dictionary<string, TesState> ByWire = Enum.GetValues<TesState>()
            .ToDictionary(s => typeof(TesState).GetField(s.ToString())!.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? s.ToString(),
                StringComparer.Ordinal);

        /// <summary>The state for a TES state name, or null when it is not one (names are upper case, as TES writes them).</summary>
        public static TesState? FromWire(string name) => ByWire.TryGetValue(name, out var state) ? state : null;
    }
}
