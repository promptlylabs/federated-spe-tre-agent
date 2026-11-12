namespace Data_Egress_API.Services
{
    /// <summary>A federated decision the Egress API refuses; the message is returned to the caller as a 400.</summary>
    public class EgressRefusedException : Exception
    {
        public EgressRefusedException(string message) : base(message)
        {
        }
    }
}
