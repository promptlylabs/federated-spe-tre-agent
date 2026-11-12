using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using FiveSafesTes.Core.Services;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.Enums;
using FiveSafesTes.Core.Models.ViewModels;
using Microsoft.AspNetCore.StaticFiles;


namespace Data_Egress_UI.Controllers
{
    [Authorize(Roles = "data-egress-admin")]
    public class DataController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private readonly IDataEgressClientHelper _dataClientHelper;

        public DataController(ILogger<HomeController> logger, IDataEgressClientHelper datahelper)
        {
            _logger = logger;
            _dataClientHelper = datahelper;
        }
   
        [HttpGet]
        public IActionResult GetAllUnprocessedEgresses()
        {
            var paramlist = new Dictionary<string, string>();

            paramlist.Add("unprocessedonly", true.ToString());
            var unprocessedfiles = _dataClientHelper.CallAPIWithoutModel<List<EgressSubmission>>("/api/DataEgress/GetAllEgresses/", paramlist).Result;
           
            return View(unprocessedfiles);

        }

        [HttpGet]
        public IActionResult GetEgress(int id)
        {
            var paramlist = new Dictionary<string, string>();
            paramlist.Add("id", id.ToString());

            var files = _dataClientHelper.CallAPIWithoutModel<EgressSubmission>("/api/DataEgress/GetEgress/", paramlist).Result;
            
            return View(files);
        }

        [HttpGet]
        public IActionResult EditFileData(int Id , int Status)
        {          
            var paramlist = new Dictionary<string, string>();
            paramlist.Add("id", Id.ToString());
            paramlist.Add("Status", Status.ToString());
            var files = _dataClientHelper.CallAPIWithoutModel<DataFiles>("/api/DataEgress/UpdateFileData/", paramlist).Result;
            return RedirectToAction("GetFiles", new { Id = Id });
        }
        [HttpPost]
        public IActionResult GetEgress(EgressSubmission model, string submitButton)
        {
            if (submitButton == "SubmitButton")
            {
                try
                {
                    var egress = _dataClientHelper.CallAPI<EgressSubmission, EgressSubmission>("/api/DataEgress/CompleteEgress/", model).Result;
                }
                catch (AggregateException ex) when (model.IsFederated)
                {
                    // The Egress API refuses an incomplete federated decision with a 400 and a message: show it on the page.
                    _logger.LogWarning(ex, "CompleteEgress refused for federated egress {Id}", model.Id);
                    var refreshed = ReloadFederatedForm(model);
                    ModelState.Clear();
                    ModelState.AddModelError(string.Empty, RefusalMessage(ex) ??
                        "The decision could not be completed. If it was saved, it has not reached the TRE yet; try Complete & Close again.");
                    return View(refreshed);
                }

            }
            else if (submitButton == "SaveButton")
            {
                var egress = _dataClientHelper.CallAPI<EgressSubmission, EgressSubmission>("/api/DataEgress/PartialEgress/", model).Result;

            }

            return RedirectToAction("GetAllEgresses", "Data", new { unprocessedonly = true });

        }
        /// <summary>The Egress API's own refusal message (a 400 with a text body), or null for any other failure.</summary>
        private static string? RefusalMessage(AggregateException ex)
        {
            if (ex.Flatten().GetBaseException() is not ApiCallException api ||
                api.StatusCode != System.Net.HttpStatusCode.BadRequest ||
                string.IsNullOrWhiteSpace(api.ResponseBody))
            {
                return null;
            }

            var body = api.ResponseBody.Trim();
            if (body.StartsWith('"'))
            {
                try
                {
                    body = System.Text.Json.JsonSerializer.Deserialize<string>(body) ?? body;
                }
                catch (System.Text.Json.JsonException)
                {
                    // Not a JSON string: show the body as it is.
                }
            }

            return string.IsNullOrWhiteSpace(body) ? null : body;
        }

        /// <summary>Reloads the stored row for re-display, keeping what the reviewer just entered. If the reload fails, the posted model is shown.</summary>
        private EgressSubmission ReloadFederatedForm(EgressSubmission posted)
        {
            try
            {
                var stored = _dataClientHelper.CallAPIWithoutModel<EgressSubmission>(
                    "/api/DataEgress/GetEgress/", new Dictionary<string, string> { { "id", posted.Id.ToString() } }).Result!;
                stored.DecisionReason = posted.DecisionReason;
                foreach (var file in stored.Files)
                {
                    var entered = posted.Files?.FirstOrDefault(f => f.Id == file.Id);
                    if (entered != null) file.Status = entered.Status;
                }
                return stored;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reload federated egress {Id}; showing the posted form", posted.Id);
                return posted;
            }
        }

        public static string GetContentType(string fileName)
        {
            // Create a new FileExtensionContentTypeProvider
            var provider = new FileExtensionContentTypeProvider();

            // Try to get the content type based on the file name's extension
            if (provider.TryGetContentType(fileName, out var contentType))
            {
                return contentType;
            }

            // If the content type cannot be determined, provide a default value
            return "application/octet-stream"; // This is a common default for unknown file types
        }

        [HttpGet]
        public IActionResult DownloadFile(int? fileId)
        {

            var paramlist = new Dictionary<string, string>
            {
                { "id", fileId.ToString() }
            };

            var egressFile = _dataClientHelper.CallAPIWithoutModel<EgressFile>($"/api/DataEgress/GetEgressFile/{fileId}").Result;
            var file = _dataClientHelper.CallAPIToGetFile(
                "/api/DataEgress/DownloadFile", paramlist).Result;
            return  File(file, GetContentType(egressFile.Name), egressFile.Name);
        }


        [HttpGet]
        public async Task<IActionResult> GetAllEgressesuUnprocessed()
        {

            var paramlist = new Dictionary<string, string>();
          
            ViewBag.PageTitle = "Unprocessed Egresses";
            paramlist.Add("unprocessedonly", true.ToString());

        
            List<EgressSubmission>? egresses  = await _dataClientHelper.CallAPIWithoutModel<List<EgressSubmission>>("/api/DataEgress/GetAllEgresses/", paramlist);

            return View(egresses);
        }

        [HttpGet]
        public IActionResult GetAllEgresses()
        {

            var paramlist = new Dictionary<string, string>();

            ViewBag.PageTitle = "All Egresses";
            paramlist.Add("unprocessedonly", false.ToString());

            List<EgressSubmission> egresses = _dataClientHelper
                .CallAPIWithoutModel<List<EgressSubmission>>("/api/DataEgress/GetAllEgresses/", paramlist).Result;

            return View(egresses);
        }



    }
}