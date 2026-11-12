using Microsoft.EntityFrameworkCore;
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.APISimpleTypeReturns;
using Microsoft.AspNetCore.Mvc;
using Data_Egress_API.Repositories.DbContexts;
using Castle.Components.DictionaryAdapter.Xml;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Microsoft.AspNetCore.Authorization;
using Serilog;
using FiveSafesTes.Core.Models.Enums;
using EasyNetQ.Management.Client.Model;
using System.IO;
using FiveSafesTes.Core.Models.ViewModels;
using Amazon.S3.Model;
using Amazon.S3;
using Amazon;
using System.Net;
using System.Text;
using System.Collections.Generic;
using FiveSafesTes.Core.Services;
using FiveSafesTes.Core.Models.FederatedTasks;
using System.Net.Mime;
using Data_Egress_API.Services;
using Microsoft.AspNetCore.StaticFiles;
using Sentry.PlatformAbstractions;
using DARE_Egress.Services;

namespace Data_Egress_API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]


    public class DataEgressController : ControllerBase
    {
        private readonly ApplicationDbContext _DbContext;

        private readonly ITreClientWithoutTokenHelper _treClientHelper;
        private readonly IMinioHelper _minioHelper;
        private readonly IDareEmailService _IDareEmailService;
        private readonly IKeyCloakService _IKeyCloakService;
        private readonly EmailSettings _EmailSettings;


        public DataEgressController(ApplicationDbContext repository, ITreClientWithoutTokenHelper treClientHelper,
            IMinioHelper minioHelper, IDareEmailService iDareEmailService, EmailSettings emailSettings, IKeyCloakService keyCloakService)
        {
            _DbContext = repository;
            _minioHelper = minioHelper;
            _treClientHelper = treClientHelper;
            _IDareEmailService = iDareEmailService;
            _EmailSettings = emailSettings;
            _IKeyCloakService = keyCloakService;
        }

        [Authorize(Roles = "data-egress-admin,dare-tre-admin")]
        [HttpPost("AddNewDataEgress")]
        public async Task<ActionResult<BoolReturn>> AddNewDataEgress(EgressSubmission submission)
        {
            try
            {
                // The reviewer is shown this text and this SHA-256, and the decision carries the SHA-256
                // back to the Agent, so they must describe the same bytes.
                if (submission.IsFederated &&
                    (submission.ResultContent == null || submission.ResultSha256 == null ||
                     FederatedResultDocumentJson.Sha256Hex(Encoding.UTF8.GetBytes(submission.ResultContent)) !=
                     submission.ResultSha256))
                {
                    Log.Warning("{Function} Refused: result does not match its SHA-256 for Submission {SubId}",
                        "AddNewDataEgress", submission.SubmissionId);
                    return BadRequest("The result does not match its SHA-256");
                }

                var existingDataFiles = _DbContext.EgressSubmissions
                    .FirstOrDefault(d => d.SubmissionId == submission.SubmissionId);


                if (existingDataFiles == null)
                {
                    _DbContext.EgressSubmissions.Add(submission);
                    foreach (var submissionFile in submission.Files)
                    {
                        submissionFile.Status = FileStatus.Undecided;
                    }

                    await _DbContext.SaveChangesAsync();
                    try
                    {
                        // With email off there is nobody to look up; with an override, the lookup is not needed.
                        var Emails = !_EmailSettings.Enabled
                            ? new List<string>()
                            : string.IsNullOrEmpty(_EmailSettings.EmailOverride) == false
                                ? new List<string>() { _EmailSettings.EmailOverride }
                                : await _IKeyCloakService.GetEmailsOfAccountWithRole("data-egress-admin");

                        foreach (var email in Emails)
                        {
                            var body = $" files are ready to review with subID {submission.Id} Name {submission.Name}";
                            await _IDareEmailService.EmailTo(email, body, body, false);
                        }

                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex,"{Function} Sending email error", "AddNewDataEgress");
                    }                  
                    
                    return new BoolReturn() { Result = true };
                }
                else
                {
                    return new BoolReturn() { Result = false };
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "AddNewDataEgress");
                throw;
            }
        }

        [Authorize(Roles = "data-egress-admin")]
        [HttpGet("GetAllEgresses")]
        public List<EgressSubmission> GetAllEgresses(bool unprocessedonly)
        {
            try
            {
                var query = _DbContext.EgressSubmissions.AsNoTracking().Include(x => x.Files).AsQueryable();
                if (unprocessedonly)
                {
                    // A federated decision stays listed until it has reached the Agent.
                    query = query.Where(x => x.Status == EgressStatus.NotCompleted ||
                                             (x.Kind == EgressSubmission.FederatedKind && !x.DecisionDelivered));
                }

                var results = query.ToList();
                // The list pages show no federated result or code; GetEgress returns them for one request.
                foreach (var egress in results)
                {
                    egress.ResultContent = null;
                    egress.Code = null;
                }

                Log.Information("{Function} {Which} Egresses retrieved successfully", "GetAllEgresses",
                    unprocessedonly ? "All Unprocessed" : "All");
                return results;
                
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "GetAllEgresses");
                throw;
            }
        }

        [Authorize(Roles = "data-egress-admin")]
        [HttpGet("GetEgress")]
        public EgressSubmission GetEgress(int id)
        {
            try
            {
                var returned = _DbContext.EgressSubmissions.First(x => x.Id == id);


                Log.Information("{Function} Files retrieved successfully", "GetEgress");
                return returned;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "GetEgress");
                throw;
            }


        }

        [Authorize(Roles = "data-egress-admin")]
        [HttpGet("GetEgressFile/{id}")]
        public EgressFile GetEgressFile(int id)
        {
            try
            {
                var returned = _DbContext.EgressFiles.First(x => x.Id == id);


                Log.Information("{Function} File retrieved successfully", "GetEgressFile");
                return returned;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "GetEgressFile");
                throw;
            }


        }

        

        [Authorize(Roles = "data-egress-admin")]
        [HttpPost("CompleteEgress")]
        public async Task<ActionResult<EgressSubmission>> CompleteEgressAsync([FromBody] EgressSubmission egress)
        {
            try
            {
                var dbegress = _DbContext.EgressSubmissions.First(x => x.Id == egress.Id);
                if (dbegress.IsFederated)
                {
                    return await CompleteFederatedEgressAsync(egress, dbegress);
                }

                if (dbegress.Status != EgressStatus.NotCompleted)
                {
                    throw new Exception("Egress has already been completed");
                }

                var approvedBy = (from x in User.Claims where x.Type == "preferred_username" select x.Value)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(approvedBy))
                {
                    approvedBy = "[Unknown]";
                }

                var approvedDate = DateTime.Now.ToUniversalTime();
                if (egress.Files.Any(x => x.Status == FileStatus.Undecided))
                {
                    throw new Exception("Not all files reviewed");
                }
                else if (egress.Files.All(x => x.Status == FileStatus.Rejected))
                {
                    dbegress.Status = EgressStatus.FullyRejected;
                }
                else if (egress.Files.All(x => x.Status == FileStatus.Approved))
                {
                    dbegress.Status = EgressStatus.FullyApproved;
                }
                else
                {
                    dbegress.Status = EgressStatus.PartiallyApproved;
                }

                var backtotre = ReviewFilesGuts(egress, dbegress, approvedBy, approvedDate);

                var result =
                    await _treClientHelper.CallAPI<EgressReview, Submission>("/api/Submission/EgressResults",
                        backtotre);
                await _DbContext.SaveChangesAsync();









                Log.Information("{Function} Egress Completed for Submission {SubId}", "CompleteEgress",
                    dbegress.SubmissionId);
                return dbegress;
            }
            catch (EgressRefusedException ex)
            {
                Log.Warning("{Function} Refused: {Reason}", "CompleteEgress", ex.Message);
                return BadRequest(ex.Message);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone else saved this request after it was read here; nothing of this call was saved.
                Log.Warning("{Function} Refused: request {Id} changed while being decided", "CompleteEgress", egress.Id);
                return BadRequest(DecidedByOtherReviewer);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "CompleteEgress");
                throw;
            }

        }

        // A federated decision is saved before the TRE Agent is called back, so a failed callback
        // never loses it. A retry re-sends the stored decision and ignores what the form now says.
        private const string DecidedByOtherReviewer =
            "This request was just decided by someone else. Reload it to see the decision.";

        private async Task<EgressSubmission> CompleteFederatedEgressAsync(EgressSubmission egress,
            EgressSubmission dbegress)
        {
            var approvedBy = (from x in User.Claims where x.Type == "preferred_username" select x.Value)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(approvedBy))
            {
                approvedBy = "[Unknown]";
            }

            if (dbegress.Status != EgressStatus.NotCompleted)
            {
                if (dbegress.DecisionDelivered)
                {
                    throw new Exception("Egress has already been completed");
                }

                // Only the reviewer who made the stored decision retries it; anyone else would
                // otherwise be told their own (possibly different) decision went through.
                if (dbegress.Reviewer != approvedBy)
                {
                    throw new EgressRefusedException(DecidedByOtherReviewer);
                }

                // A saved decision is final. Sending the same form again resends it; a different one is refused,
                // so the reviewer is never told a decision went through that was not the one sent.
                if (!SameDecision(egress, dbegress))
                {
                    throw new EgressRefusedException(
                        $"This request was already decided ({StoredDecision(dbegress)}). The decision is saved and is sent to the TRE automatically; it cannot be changed.");
                }

                Log.Information("{Function} Re-sending stored decision for Submission {SubId}", "CompleteEgress",
                    dbegress.SubmissionId);
                return await FederatedDecisionDelivery.DeliverAsync(_DbContext, _treClientHelper, dbegress);
            }

            // The form must decide every file of the row, so the status and the reason rule
            // below cover the whole result and not just the files the form happens to post.
            var formFileIds = (egress.Files ?? new List<EgressFile>()).Select(x => x.Id).OrderBy(x => x).ToList();
            var rowFileIds = dbegress.Files.Select(x => x.Id).OrderBy(x => x).ToList();
            if (rowFileIds.Count == 0 || !formFileIds.SequenceEqual(rowFileIds))
            {
                throw new EgressRefusedException("The review must decide every file of the federated result");
            }

            if (egress.Files.Any(x => x.Status == FileStatus.Undecided))
            {
                throw new EgressRefusedException("Not all files reviewed");
            }

            if (egress.Files.Any(x => x.Status == FileStatus.Rejected) &&
                string.IsNullOrWhiteSpace(egress.DecisionReason))
            {
                throw new EgressRefusedException("A reason is required to reject a federated result");
            }

            if (egress.Files.All(x => x.Status == FileStatus.Rejected))
            {
                dbegress.Status = EgressStatus.FullyRejected;
            }
            else if (egress.Files.All(x => x.Status == FileStatus.Approved))
            {
                dbegress.Status = EgressStatus.FullyApproved;
            }
            else
            {
                dbegress.Status = EgressStatus.PartiallyApproved;
            }

            dbegress.DecisionReason = string.IsNullOrWhiteSpace(egress.DecisionReason)
                ? null
                : egress.DecisionReason.Trim();
            dbegress.DecisionDelivered = false;
            ReviewFilesGuts(egress, dbegress, approvedBy, DateTime.Now.ToUniversalTime());
            await _DbContext.SaveChangesAsync();

            return await FederatedDecisionDelivery.DeliverAsync(_DbContext, _treClientHelper, dbegress);
        }

        private static bool SameDecision(EgressSubmission form, EgressSubmission stored)
        {
            var reason = string.IsNullOrWhiteSpace(form.DecisionReason) ? null : form.DecisionReason.Trim();
            var formFiles = form.Files ?? new List<EgressFile>();
            return reason == stored.DecisionReason &&
                   formFiles.Count == stored.Files.Count &&
                   stored.Files.All(f => formFiles.Any(x => x.Id == f.Id && x.Status == f.Status));
        }

        private static string StoredDecision(EgressSubmission stored) => stored.Status switch
        {
            EgressStatus.FullyApproved => "Approved",
            EgressStatus.FullyRejected => $"Rejected: {stored.DecisionReason}",
            _ => $"{stored.EgressStatusDisplay}: {stored.DecisionReason}"
        };

        private static EgressReview ReviewFilesGuts(EgressSubmission egress, EgressSubmission dbegress,
            string approvedBy,
            DateTime approvedDate)
        {
            dbegress.Reviewer = approvedBy;
            dbegress.Completed = approvedDate;

            var backtotre = new EgressReview()
            {
                SubId = dbegress.SubmissionId,
                FileResults = new List<EgressResult>(),
                OutputBucket = dbegress.OutputBucket
            };
            foreach (var file in egress.Files)
            {
                var dbegressfile = dbegress.Files.First(x => x.Id == file.Id);
                dbegressfile.Status = file.Status;
                backtotre.FileResults.Add(new EgressResult()
                {
                    FileName = dbegressfile.Name,
                    Approved = dbegressfile.Status == FileStatus.Approved,
                });
                var egfile = dbegress.Files.First(x => x.Id == file.Id);
                egfile.Status = file.Status;
                egfile.LastUpdate = approvedDate;
                egfile.Reviewer = approvedBy;
            }

            return backtotre;
        }

        [Authorize(Roles = "data-egress-admin")]
        [HttpPost("PartialEgress")]
        public async Task<EgressSubmission> PartialEgressAsync([FromBody] EgressSubmission egress)
        {

            var dbegress = _DbContext.EgressSubmissions.First(x => x.Id == egress.Id);
            if (dbegress.Status != EgressStatus.NotCompleted)
            {
                throw new Exception("Egress has already been completed");
            }

            var approvedBy =
                (from x in User.Claims where x.Type == "preferred_username" select x.Value).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(approvedBy))
            {
                approvedBy = "[Unknown]";
            }

            var approvedDate = DateTime.Now.ToUniversalTime();

            var backtotre = ReviewFilesGuts(egress, dbegress, approvedBy, approvedDate);


            await _DbContext.SaveChangesAsync();









            Log.Information("{Function} Egress Completed for Submission {SubId}", "CompleteEgress",
                dbegress.SubmissionId);
            return dbegress;
        }



        [Authorize(Roles = "data-egress-admin")]
        [HttpPost("UpdateFileData")]
        public async Task<EgressFile> UpdateFileData(int fileId, FileStatus status)
        {
            try
            {
                var approvedBy = (from x in User.Claims where x.Type == "preferred_username" select x.Value)
                    .FirstOrDefault();
                if (string.IsNullOrWhiteSpace(approvedBy))
                {
                    approvedBy = "[Unknown]";
                }

                var approvedDate = DateTime.Now.ToUniversalTime();

                var returned = _DbContext.EgressFiles.First(x => x.Id == fileId);

                // A completed federated decision is final, even while its callback is still pending.
                if (returned.EgressSubmission?.IsFederated == true &&
                    returned.EgressSubmission.Status != EgressStatus.NotCompleted)
                {
                    throw new Exception("Egress has already been completed");
                }

                returned.Status = status;
                returned.Reviewer = approvedBy;
                returned.LastUpdate = approvedDate;


                await _DbContext.SaveChangesAsync();
                Log.Information("{Function} File updated", "UpdateFileData");
                return returned;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "UpdateFileData");
                throw;
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

        [Authorize(Roles = "data-egress-admin")]
        [HttpGet("DownloadFile")]
        public async Task<IActionResult> DownloadFileAsync(int id)
        {
            try
            {

                var egressFile = _DbContext.EgressFiles.First(x => x.Id == id);

                // A federated result is held in the database, not in the bucket.
                if (egressFile.EgressSubmission?.IsFederated == true)
                {
                    return File(Encoding.UTF8.GetBytes(egressFile.EgressSubmission.ResultContent ?? string.Empty),
                        "application/json", egressFile.Name);
                }

                var response = await _minioHelper.GetCopyObject(egressFile.EgressSubmission.OutputBucket, egressFile.Name);

                var responseStream = response.ResponseStream;

                return File(responseStream, GetContentType(egressFile.Name), egressFile.Name);
                
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "DownloadFiles");
                throw;
            }

        }





    }

}

