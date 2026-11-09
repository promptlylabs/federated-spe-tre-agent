using System.Security.Claims;
using FiveSafesTes.Core.Models;
using Microsoft.AspNetCore.Authentication;
using Serilog;
using Submission.Api.Repositories.DbContexts;
using Submission.Api.Services.Contract;

namespace Submission.Api.Services
{
    public class ControllerHelpers
    {

        public static async Task AddUserToMinioBucket(User user, Project project,
            IHttpContextAccessor httpContextAccessor, string attributeName,
            IKeycloakMinioUserService keycloakMinioUserService, ClaimsPrincipal loggedInUser,
            ApplicationDbContext dbContext)
        {
            // NOTE: When the access token here expired, the process will crash, so we need to renovate the way we get/handle access token
            var accessToken = await httpContextAccessor.HttpContext.GetTokenAsync("access_token");


            await keycloakMinioUserService.SetMinioUserAttribute(accessToken, user.Name.ToString(), attributeName,
                project.SubmissionBucket.ToLower() + "_policy");

            await keycloakMinioUserService.SetMinioUserAttribute(accessToken, user.Name.ToString(), attributeName,
                project.OutputBucket.ToLower() + "_policy");
            



        }


        public static Tre? GetUserTre(ClaimsPrincipal loggedInUser, ApplicationDbContext dbContext)
        {
            var usersName = (from x in loggedInUser.Claims where x.Type == "preferred_username" select x.Value).First();
            var tre = dbContext.Tres.FirstOrDefault(x => (x.AdminUsername.ToLower() == usersName.ToLower()) || (("service-account-" + x.KeycloakClientId).ToLower() == usersName.ToLower()));
            if (tre == null)
            {
                throw new Exception("User " + usersName + " doesn't have a tre");
            }

            return tre;
        }

        /// <summary>
        /// Resolves the TRE whose Keycloak service account made this request, or null. Unlike GetUserTre it does not
        /// accept Tre.AdminUsername, so a human TRE admin is never taken for the Agent. Requires the token's azp to
        /// equal the TRE's KeycloakClientId and preferred_username to be service-account-&lt;KeycloakClientId&gt;.
        /// </summary>
        public static Tre? GetAgentTre(ClaimsPrincipal user, ApplicationDbContext db)
        {
            var azp = user.FindFirst("azp")?.Value ?? user.FindFirst("client_id")?.Value;
            var username = user.FindFirst("preferred_username")?.Value;
            if (string.IsNullOrEmpty(azp) || string.IsNullOrEmpty(username))
            {
                return null;
            }

            var candidates = db.Tres.Where(x => x.KeycloakClientId == azp).ToList();
            return candidates.FirstOrDefault(x =>
                !string.IsNullOrEmpty(x.KeycloakClientId) &&
                string.Equals(x.KeycloakClientId, azp, StringComparison.Ordinal) &&
                string.Equals("service-account-" + x.KeycloakClientId, username, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Determine whether a user has a TRE without throwing an exception.
        /// </summary>
        public static bool IsUserAssignedTRE(ClaimsPrincipal loggedInUser, ApplicationDbContext dbContext)
        {
            try
            {
                Tre? tre = GetUserTre(loggedInUser, dbContext);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static async Task RemoveUserFromMinioBucket(User user, Project project,
            IHttpContextAccessor httpContextAccessor, string attributeName,
            IKeycloakMinioUserService keycloakMinioUserService, ClaimsPrincipal loggedInUser,
            ApplicationDbContext dbContext)
        {
            // NOTE: When the access token here expired, the process will crash, so we need to renovate the way we get/handle access token
            var accessToken = await httpContextAccessor.HttpContext.GetTokenAsync("access_token");



            await keycloakMinioUserService.RemoveMinioUserAttribute(accessToken, user.Name.ToString(), attributeName,
                project.SubmissionBucket.ToLower() + "_policy");
            await keycloakMinioUserService.RemoveMinioUserAttribute(accessToken, user.Name.ToString(), attributeName,
                project.OutputBucket.ToLower() + "_policy");

            


        }

        public static async Task AddAuditLog(LogType logType, User? user, Project? project, Tre? tre, FiveSafesTes.Core.Models.Submission? submission, string? formData,
            IHttpContextAccessor httpContextAccessor,
            ClaimsPrincipal loggedInUser, ApplicationDbContext dbContext)
        {
            var audit = new AuditLog()
            {
                HistoricFormData = formData,
                Submission = submission,
                IPaddress = httpContextAccessor.HttpContext.Connection.RemoteIpAddress.ToString(),
                LoggedInUserName = (from x in loggedInUser.Claims where x.Type == "preferred_username" select x.Value).First(),
                Project = project,
                User = user,
                Tre = tre,
                LogType = logType,
                Date = DateTime.Now.ToUniversalTime()
            };
            dbContext.AuditLogs.Add(audit);
            await dbContext.SaveChangesAsync();
            Log.Information(
                "{Function}: AuditLogs: LogType: {LogType,} UserId: {UserId}, ProjectId: {ProjectId}, TreId: {TreId}, SubmissionId: {SubmissionId}, FormData {FormData}, LoggedInUser: {LoggedInUser}",
                "AddAuditLog", logType.ToString(), user == null ? "[null]" : user.Id, project == null ? "[null]" : project.Id,
                tre == null ? "[null]" : tre.Id, submission == null ? "[null]" : submission.Id, formData == null ? "[null]" : formData,
                (from x in loggedInUser.Claims where x.Type == "preferred_username" select x.Value).First());
        }
    }
}
