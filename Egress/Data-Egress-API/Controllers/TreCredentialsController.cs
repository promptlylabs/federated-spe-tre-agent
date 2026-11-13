
using FiveSafesTes.Core.Models;
using FiveSafesTes.Core.Models.APISimpleTypeReturns;
using FiveSafesTes.Core.Models.Settings;
using FiveSafesTes.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Data_Egress_API.Repositories.DbContexts;
using Newtonsoft.Json;


namespace Data_Egress_API.Controllers
{
    [Route("api/[controller]")]
    
    
    [ApiController]
    public class TreCredentialsController : Controller
    {

        private readonly ApplicationDbContext _DbContext;
        private readonly IEncDecHelper _encDecHelper;
        
        public KeycloakTokenHelper _keycloakTokenHelper { get; set; }
        public KeycloakTokenHelper _egressKeycloakTokenHelper { get; set; }

        private DataEgressKeyCloakSettings egressKeyCloakSettings;

        private TreKeyCloakSettings keycloakSettings;


        public TreCredentialsController(ApplicationDbContext applicationDbContext, IEncDecHelper encDec, TreKeyCloakSettings keycloakSettings, DataEgressKeyCloakSettings egressKeyCloakSettings)
        {
            this.egressKeyCloakSettings = egressKeyCloakSettings;
            this.keycloakSettings = keycloakSettings;
            _encDecHelper = encDec;
            _DbContext = applicationDbContext;
            _keycloakTokenHelper = new KeycloakTokenHelper(keycloakSettings.BaseUrl, keycloakSettings.ClientId,
                keycloakSettings.ClientSecret, keycloakSettings.Proxy, keycloakSettings.ProxyAddresURL, keycloakSettings.KeycloakDemoMode);

            _egressKeycloakTokenHelper = new KeycloakTokenHelper(egressKeyCloakSettings.BaseUrl, egressKeyCloakSettings.ClientId,
                egressKeyCloakSettings.ClientSecret, egressKeyCloakSettings.Proxy, egressKeyCloakSettings.ProxyAddresURL, egressKeyCloakSettings.KeycloakDemoMode);

        }


        [Authorize(Roles = "data-egress-admin")]
        [HttpGet("CheckCredentialsAreValid")]
        public async Task<BoolReturn> CheckCredentialsAreValidAsync()
        {
            try
            {
                var result = new BoolReturn() { Result = false };
                var creds = _DbContext.KeycloakCredentials.FirstOrDefault(x => x.CredentialType == CredentialType.Tre);
                if (creds != null)
                {
                    var token = await _keycloakTokenHelper.GetTokenForUser(creds.UserName,
                        _encDecHelper.Decrypt(creds.PasswordEnc), "data-egress-admin");
                    result.Result = !string.IsNullOrWhiteSpace(token.token);

                }

                return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "CheckCredentialsAreValid");
                throw;
            }
        }

        [Authorize(Roles = "data-egress-admin")]
        [HttpGet("EgressCheckCredentialsAreValid")]
        public async Task<BoolReturn> EgressCheckCredentialsAreValidAsync()
        {
            try { 
            var result = new BoolReturn() { Result = false };
            var creds = _DbContext.KeycloakCredentials.FirstOrDefault(x => x.CredentialType == CredentialType.Egress);
            if (creds != null)
            {
                var token = await _egressKeycloakTokenHelper.GetTokenForUser(creds.UserName,
                    _encDecHelper.Decrypt(creds.PasswordEnc), "data-egress-admin");
                result.Result = !string.IsNullOrWhiteSpace(token.token);

            }

            return result;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crashed", "EgressCheckCredentialsAreValid");
                throw;
            }
        }

        [Authorize(Roles = "data-egress-admin")]
        [HttpPost("EgressUpdateCredentials")]
        public async Task<KeycloakCredentials> EgressUpdateCredentials(KeycloakCredentials creds)
        {
            try
            {
                creds.Valid = true;
                var token = await _egressKeycloakTokenHelper.GetTokenForUser(creds.UserName,
                    creds.PasswordEnc, "data-egress-admin");
                if (string.IsNullOrWhiteSpace(token.token))
                {
                    creds.ErrorMessage = token.Errorstring;
                    creds.Valid = false;
                    return creds;
                }

                await SaveCredentialsAsync(creds, CredentialType.Egress);

                Log.Information("{Function} Credentials Successfully update", "EgressUpdateCredentials");
                return creds;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crash", "EgressUpdateCredentials");
                throw;
            }
        }

        /// <summary>
        /// Stores the login of the given type, replacing the stored one if there is one. The stored row is updated
        /// in place: attaching a second instance with the same key would make EF refuse the save.
        /// </summary>
        private async Task SaveCredentialsAsync(KeycloakCredentials creds, CredentialType type)
        {
            creds.CredentialType = type;
            creds.PasswordEnc = _encDecHelper.Encrypt(creds.PasswordEnc);

            var dbcred = _DbContext.KeycloakCredentials.FirstOrDefault(x => x.CredentialType == type);
            if (dbcred == null)
            {
                _DbContext.KeycloakCredentials.Add(creds);
            }
            else
            {
                dbcred.UserName = creds.UserName;
                dbcred.PasswordEnc = creds.PasswordEnc;
                creds.Id = dbcred.Id;
            }

            await _DbContext.SaveChangesAsync();
        }

        [Authorize(Roles = "data-egress-admin")]
        [HttpPost("UpdateCredentials")]
        public async Task<KeycloakCredentials> UpdateCredentials(KeycloakCredentials creds)
        {
            try
            {
                creds.Valid = true;
                var token = await _keycloakTokenHelper.GetTokenForUser(creds.UserName,
                    creds.PasswordEnc, "data-egress-admin");
                if (string.IsNullOrWhiteSpace(token.token))
                {
                    creds.ErrorMessage= token.Errorstring;
                    creds.Valid = false;
                    return creds;
                }

                await SaveCredentialsAsync(creds, CredentialType.Tre);

                Log.Information("{Function} Credentials Successfully update", "UpdateCredentials");
                return creds;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Function} Crash", "UpdateCredentials");
                throw;
            }
        }
    }
}
