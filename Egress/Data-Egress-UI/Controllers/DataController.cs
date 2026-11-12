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
        private readonly IConfiguration _configuration;

        public DataController(ILogger<HomeController> logger, IDataEgressClientHelper datahelper, IConfiguration configuration)
        {
            _logger = logger;
            _dataClientHelper = datahelper;
            _configuration = configuration;
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
            // Get S3 settings from configuration
            var s3Url = _configuration["S3Settings:Url"] ?? "http://localhost:9003";
            var s3BucketPath = _configuration["S3Settings:BucketPath"] ?? "/rustfs/console/browser/?bucket=";
            ViewBag.S3Url = s3Url;
            ViewBag.S3BucketPath = s3BucketPath;
            
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
                    if (model.IsFederated)
                    {
                        // A completed-but-undelivered row is re-sent from the stored decision, so the form is not checked then.
                        var reloaded = ReloadFederatedForm(model);
                        if (reloaded.Status == EgressStatus.NotCompleted)
                        {
                            var files = model.Files ?? new List<EgressFile>();
                            var errors = new List<string>();
                            if (files.Any(f => f.Status == FileStatus.Rejected) && string.IsNullOrWhiteSpace(model.DecisionReason))
                                errors.Add("A reason is required to reject this result.");
                            if (files.Any(f => f.Status == FileStatus.Undecided))
                                errors.Add("Decide every file before completing.");
                            if (errors.Count > 0)
                            {
                                ModelState.Clear();
                                foreach (var e in errors) ModelState.AddModelError(string.Empty, e);
                                return View(reloaded);
                            }
                        }
                    }
                    var egress = _dataClientHelper.CallAPI<EgressSubmission, EgressSubmission>("/api/DataEgress/CompleteEgress/", model).Result;
                }
                catch (AggregateException ex) when (model.IsFederated)
                {
                    // Federated rows are refused with a message (reason missing, files mismatch): show it on the page.
                    _logger.LogWarning(ex, "CompleteEgress refused for federated egress {Id}", model.Id);
                    var refreshed = ReloadFederatedForm(model);
                    ModelState.Clear();
                    ModelState.AddModelError(string.Empty, "The decision could not be completed. If it was saved, it has not reached the TRE yet; try Complete & Close again.");
                    return View(refreshed);
                }

            }
            else if (submitButton == "SaveButton")
            {
                var egress = _dataClientHelper.CallAPI<EgressSubmission, EgressSubmission>("/api/DataEgress/PartialEgress/", model).Result;

            }

            return RedirectToAction("GetAllEgresses", "Data", new { unprocessedonly = true });

        }
        /// <summary>Reloads the stored row for re-display, keeping what the reviewer just entered.</summary>
        private EgressSubmission ReloadFederatedForm(EgressSubmission posted)
        {
            var stored = _dataClientHelper.CallAPIWithoutModel<EgressSubmission>(
                "/api/DataEgress/GetEgress/", new Dictionary<string, string> { { "id", posted.Id.ToString() } }).Result!;
            stored.DecisionReason = posted.DecisionReason;
            foreach (var file in stored.Files)
            {
                var entered = posted.Files?.FirstOrDefault(f => f.Id == file.Id);
                if (entered != null) file.Status = entered.Status;
            }
            ViewBag.S3Url = _configuration["S3Settings:Url"] ?? "http://localhost:9003";
            ViewBag.S3BucketPath = _configuration["S3Settings:BucketPath"] ?? "/rustfs/console/browser/?bucket=";
            return stored;
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