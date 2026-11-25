using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.Settings;
using FiveSafesTes.Core.Services;
using Microsoft.Extensions.Options;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Gets a client-credentials token for this Agent's service account from Keycloak: the TRE's, or the aggregator's.
    /// </summary>
    public class KeycloakFederatedTokenSource : IFederatedTokenSource
    {
        private readonly IOptionsMonitor<SubmissionKeyCloakSettings> _keycloak;
        private readonly IConfiguration _config;
        private readonly Agent.Api.Models.FederatedTaskSettings _settings;

        public KeycloakFederatedTokenSource(IOptionsMonitor<SubmissionKeyCloakSettings> keycloak, IConfiguration config,
            Agent.Api.Models.FederatedTaskSettings settings)
        {
            _keycloak = keycloak;
            _config = config;
            _settings = settings;
        }

        public async Task<string> GetTokenAsync()
        {
            var s = _keycloak.CurrentValue;
            var demoMode = KeycloakCommon.ResolveKeycloakDemoMode(s.KeycloakDemoMode, _config["KeycloakDemoMode"]);
            var helper = new KeycloakTokenHelper(s.BaseUrl, s.ClientId, s.ClientSecret, s.Proxy, s.ProxyAddresURL, demoMode, isServiceAccount: true);
            var (token, error) = await helper.GetTokenForUser(s.ClientId, s.ClientSecret, _settings.SubmissionRole);
            // The helper reports failures as an empty token plus a message; never let an empty token reach Submission.
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException($"Could not get a token for the Agent's service account: {error}");
            return token;
        }
    }
}
