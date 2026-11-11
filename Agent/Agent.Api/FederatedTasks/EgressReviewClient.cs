using System.Net.Http.Headers;
using System.Text;
using FiveSafesTes.Core.Models.Settings;
using FiveSafesTes.Core.Services;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Posts a federated result to the Egress app as a "federated" egress submission,
    /// with a client-credentials token for the Agent's Egress service account.
    /// </summary>
    public class EgressReviewClient : IEgressReviewClient
    {
        private const string Endpoint = "/api/DataEgress/AddNewDataEgress/";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<DataEgressKeyCloakSettings> _keycloak;
        private readonly ApiEndpointSettings _endpoints;
        private readonly IConfiguration _config;

        public EgressReviewClient(
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<DataEgressKeyCloakSettings> keycloak,
            IOptions<ApiEndpointSettings> endpoints,
            IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _keycloak = keycloak;
            _endpoints = endpoints.Value;
            _config = config;
        }

        public bool IsConfigured
        {
            get
            {
                var s = _keycloak.CurrentValue;
                return !string.IsNullOrEmpty(s.ClientId) && !string.IsNullOrEmpty(s.ClientSecret);
            }
        }

        public async Task RegisterAsync(EgressRegistration r, CancellationToken ct)
        {
            var submission = new
            {
                SubmissionId = r.TaskId,
                r.Name,
                Kind = "federated",
                Study = new { Id = r.StudyId },
                r.Code,
                r.ResultContent,
                r.ResultSha256,
                // The Egress app sends it back with the decision, and the Agent's EgressResults requires it.
                r.OutputBucket,
                Files = new[] { new { Name = $"{r.TaskId}.json", Status = 0 } }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoints.EgressApiUrl + Endpoint)
            {
                Content = new StringContent(JsonConvert.SerializeObject(submission), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync());

            using var response = await _httpClientFactory.CreateClient().SendAsync(request, ct);
            // The body is BoolReturn; Result = false means the row already exists, which is also success.
            response.EnsureSuccessStatusCode();
        }

        protected virtual async Task<string> GetTokenAsync()
        {
            var s = _keycloak.CurrentValue;
            var demoMode = KeycloakCommon.ResolveKeycloakDemoMode(s.KeycloakDemoMode, _config["KeycloakDemoMode"]);
            var helper = new KeycloakTokenHelper(s.BaseUrl, s.ClientId, s.ClientSecret, s.Proxy, s.ProxyAddresURL, demoMode, isServiceAccount: true);
            var (token, error) = await helper.GetTokenForUser(s.ClientId, s.ClientSecret, "dare-tre-admin");
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException($"Could not get a token for the Egress service account: {error}");
            return token;
        }
    }
}
