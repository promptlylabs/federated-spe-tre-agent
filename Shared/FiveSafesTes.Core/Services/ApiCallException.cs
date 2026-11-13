using System.Net;

namespace FiveSafesTes.Core.Services
{
    /// <summary>A failed API call that keeps the status code and the response body for callers that want to show them.</summary>
    public class ApiCallException : Exception
    {
        public HttpStatusCode StatusCode { get; }
        public string? ResponseBody { get; }

        public ApiCallException(string message, HttpStatusCode statusCode, string? responseBody) : base(message)
        {
            StatusCode = statusCode;
            ResponseBody = responseBody;
        }
    }
}
