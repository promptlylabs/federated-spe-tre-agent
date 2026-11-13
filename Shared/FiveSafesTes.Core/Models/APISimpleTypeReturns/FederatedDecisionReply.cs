namespace FiveSafesTes.Core.Models.APISimpleTypeReturns
{
    /// <summary>
    /// The TRE Agent's answer to a federated decision on EgressResults. A refusal that sending the same decision again
    /// cannot change (400, 404, 409) carries <see cref="FinalRefusal"/>, so the Egress app can tell it from the same
    /// status coming from a proxy, an ingress or a wrong address, which it keeps retrying.
    /// </summary>
    public class FederatedDecisionReply : BoolReturn
    {
        public const string FinalRefusal = "final_refusal_by_tre_agent";

        public string? Refusal { get; set; }

        public static bool IsFinal(int status) => status is 400 or 404 or 409;
    }
}
