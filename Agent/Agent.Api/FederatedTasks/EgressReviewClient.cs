using System.Net.Http.Headers;
using System.Text;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.Enums;
using FiveSafesTes.Core.Models.Settings;
using FiveSafesTes.Core.Services;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Posts a federated result to the Egress app as a "federated" EgressSubmission (the model both apps share),
    /// with a client-credentials token for the Agent's Egress service account.
    /// </summary>
    public class EgressReviewClient : IEgressReviewClient
    {
        private const string Endpoint = "/api/DataEgress/AddNewDataEgress/";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<FederatedEgressSettings> _keycloak;
        private readonly ApiEndpointSettings _endpoints;
        private readonly IConfiguration _config;

        public EgressReviewClient(
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<FederatedEgressSettings> keycloak,
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
            get { return _keycloak.CurrentValue.IsComplete; }
        }

        public async Task RegisterAsync(EgressRegistration r, CancellationToken ct)
        {
            var submission = new EgressSubmission
            {
                SubmissionId = r.TaskId,
                Name = r.Name,
                Status = EgressStatus.NotCompleted,
                Kind = "federated",
                Study = new FederatedStudy { Id = r.StudyId },
                Code = r.Code,
                ResultContent = r.ResultContent,
                ResultSha256 = r.ResultSha256,
                // The Egress app sends it back with the decision, and the Agent's EgressResults requires it.
                OutputBucket = r.OutputBucket,
                Files = new List<EgressFile> { new() { Name = $"{r.TaskId}.json", Status = FileStatus.Undecided } }
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
