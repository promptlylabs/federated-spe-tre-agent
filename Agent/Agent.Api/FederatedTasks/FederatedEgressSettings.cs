using FiveSafesTes.Core.Models.Settings;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// The Agent's own service account in the Egress app's Keycloak realm, used to register federated results for
    /// review. Separate from <see cref="DataEgressKeyCloakSettings"/>, which native egress uses, and with no defaults:
    /// until <see cref="BaseKeyCloakSettings.BaseUrl"/>, ClientId and ClientSecret are all set, results are held.
    /// </summary>
    public class FederatedEgressSettings : BaseKeyCloakSettings
    {
        public const string SectionName = "FederatedEgressSettings";

        public bool IsComplete =>
            !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ClientId) &&
            !string.IsNullOrWhiteSpace(ClientSecret);
    }
}
