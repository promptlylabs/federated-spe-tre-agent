using System.Net.Http.Headers;
using FiveSafesTes.Core.Models.Enums;
using FiveSafesTes.Core.Models.FederatedTasks;
using FiveSafesTes.Core.Models.Settings;
using FiveSafesTes.Core.Models.Tes;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Agent.Api.FederatedTasks
{
    public class FederatedTaskClient : IFederatedTaskClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IOptionsMonitor<SubmissionKeyCloakSettings> _keycloak;
        private readonly ApiEndpointSettings _endpoints;
        private readonly IFederatedTokenSource _tokens;

        public FederatedTaskClient(
            IHttpClientFactory httpClientFactory,
            IOptionsMonitor<SubmissionKeyCloakSettings> keycloak,
            IOptions<ApiEndpointSettings> endpoints,
            IFederatedTokenSource tokens)
        {
            _httpClientFactory = httpClientFactory;
            _keycloak = keycloak;
            _endpoints = endpoints.Value;
            _tokens = tokens;
        }

        public bool IsConfigured
        {
            get
            {
                var s = _keycloak.CurrentValue;
                return s.ConfigInputMethod == ConfigInputMethod.Upload
                       && !string.IsNullOrEmpty(s.ClientId)
                       && !string.IsNullOrEmpty(s.ClientSecret);
            }
        }

        public async Task<List<TesTask>> GetPendingAsync()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url("/api/FederatedTask/GetPendingTasksForTre"));
            await Authorize(request);

            using var response = await _httpClientFactory.CreateClient().SendAsync(request);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<List<TesTask>>(body) ?? new List<TesTask>();
        }

        public async Task<FederatedReportOutcome> ReportAsync(FederatedTaskResultReport report, byte[]? resultFile)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(report.TaskId), FederatedReportFields.TaskId);
            form.Add(new StringContent(WireState(report.State)), FederatedReportFields.State);

            if (report.ErrorCode != null)
                form.Add(new StringContent(report.ErrorCode), FederatedReportFields.ErrorCode);
            if (report.ErrorMessage != null)
                form.Add(new StringContent(report.ErrorMessage), FederatedReportFields.ErrorMessage);

            // For COMPLETE the hash comes from the result document, so the form field is for error states only.
            if (report.State != TesState.COMPLETEEnum && report.DatasetSha256 != null)
                form.Add(new StringContent(report.DatasetSha256), FederatedReportFields.DatasetSha256);

            if (resultFile != null)
            {
                var file = new ByteArrayContent(resultFile);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                form.Add(file, FederatedReportFields.ResultFile, "result.json");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, Url("/api/FederatedTask/ReportResult")) { Content = form };
            await Authorize(request);

            using var response = await _httpClientFactory.CreateClient().SendAsync(request);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync();

            if (status == 200)
            {
                var ok = TryParse<FederatedReportResponse>(body);
                return new FederatedReportOutcome(status, null, null, ok?.ResultKey, ok?.ResultSha256);
            }

            var error = TryParse<FederatedApiError>(body);
            // A body that parses but has no error_code is not ours (e.g. "{}" from a proxy).
            if (error == null || string.IsNullOrEmpty(error.ErrorCode))
                return new FederatedReportOutcome(status, null, null, null, null);
            return new FederatedReportOutcome(status, error.ErrorCode, error.ErrorMessage, null, error.ResultSha256);
        }

        private string Url(string endpoint) => _endpoints.SubmissionApiUrl + endpoint;

        private async Task Authorize(HttpRequestMessage request)
        {
            var token = await _tokens.GetTokenAsync();
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("No token for the TRE service account");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        private static T? TryParse<T>(string body) where T : class
        {
            try
            {
                return JsonConvert.DeserializeObject<T>(body);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string WireState(TesState state) => state switch
        {
            TesState.COMPLETEEnum => "COMPLETE",
            TesState.EXECUTORERROREnum => "EXECUTOR_ERROR",
            TesState.SYSTEMERROREnum => "SYSTEM_ERROR",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "not a federated report state")
        };
    }
}
