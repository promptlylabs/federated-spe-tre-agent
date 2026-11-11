namespace BL.Models
{
    /// <summary>
    /// The study a federated task was run for, as the TRE Agent sends it with a review request.
    /// Stored as JSON on the egress row.
    /// </summary>
    public class FederatedStudy
    {
        public string Id { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? Researcher { get; set; }
        public virtual DataPermit? DataPermit { get; set; }
    }

    public class DataPermit
    {
        public string? Identifier { get; set; }
        public string? Issuer { get; set; }
        public string? Purpose { get; set; }
        public string? PrincipalInvestigator { get; set; }
        public string[]? AuthorisedPersons { get; set; }
        public string[]? DataCategories { get; set; }
        public DateOnly? ValidFrom { get; set; }
        public DateOnly? ValidUntil { get; set; }
        public string? Conditions { get; set; }
    }
}
