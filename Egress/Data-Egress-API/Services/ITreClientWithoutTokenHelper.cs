using FiveSafesTes.Core.Services;

namespace Data_Egress_API.Services
{
    public interface ITreClientWithoutTokenHelper: IBaseClientHelper
    {

        bool CheckCredsAreAvailable();


    }
}
