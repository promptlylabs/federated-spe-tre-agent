using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.Settings;
using FiveSafesTes.Core.Services;
using Microsoft.Extensions.Options;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Gets a client-credentials token for the TRE service account from Keycloak.
    /// </summary>
    public class KeycloakFederatedTokenSource : IFederatedTokenSource
    {
        private readonly IOptionsMonitor<SubmissionKeyCloakSettings> _keycloak;
        private readonly IConfiguration _config;

        public KeycloakFederatedTokenSource(IOptionsMonitor<SubmissionKeyCloakSettings> keycloak, IConfiguration config)
        {
            _keycloak = keycloak;
            _config = config;
        }

        public async Task<string> GetTokenAsync()
        {
            var s = _keycloak.CurrentValue;
            var demoMode = KeycloakCommon.ResolveKeycloakDemoMode(s.KeycloakDemoMode, _config["KeycloakDemoMode"]);
            var helper = new KeycloakTokenHelper(s.BaseUrl, s.ClientId, s.ClientSecret, s.Proxy, s.ProxyAddresURL, demoMode, isServiceAccount: true);
            var (token, error) = await helper.GetTokenForUser(s.ClientId, s.ClientSecret, "dare-tre-admin");
            // The helper reports failures as an empty token plus a message; never let an empty token reach Submission.
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException($"Could not get a token for the TRE service account: {error}");
            return token;
        }
    }
}
