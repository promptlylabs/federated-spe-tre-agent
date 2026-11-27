using System.Runtime.Serialization;
using FiveSafesTes.Core.Utilities;

namespace FiveSafesTes.Core.Models.Tes
{
    /// <summary>
    /// TES 1.1 service-info: a GA4GH service-info (id, name, type, organization, version, ...) plus the TES fields storage and
    /// tesResources_backend_parameters.
    /// </summary>
    [DataContract]
    public class TesServiceInfo
    {
        public TesServiceInfo() => NewtonsoftJsonSafeInit.SetDefaultSettings();

        /// <summary>Unique id of this service, in reverse domain name format.</summary>
        [DataMember(Name = "id")]
        public string Id { get; set; } = string.Empty;

        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "type")]
        public TesServiceType Type { get; set; } = new();

        [DataMember(Name = "description", EmitDefaultValue = false)]
        public string? Description { get; set; }

        [DataMember(Name = "organization")]
        public TesServiceOrganization Organization { get; set; } = new();

        [DataMember(Name = "documentationUrl", EmitDefaultValue = false)]
        public string? DocumentationUrl { get; set; }

        /// <summary>Version of the service (not of the TES API, which is in <see cref="Type"/>).</summary>
        [DataMember(Name = "version")]
        public string Version { get; set; } = string.Empty;

        [DataMember(Name = "storage")]
        public List<string> Storage { get; set; } = [];

        [DataMember(Name = "tesResources_backend_parameters")]
        public List<string> TesResourcesSupportedBackendParameters { get; set; } = [];
    }

    /// <summary>The GA4GH service type: group org.ga4gh, artifact tes, and the TES API version.</summary>
    [DataContract]
    public class TesServiceType
    {
        [DataMember(Name = "group")]
        public string Group { get; set; } = "org.ga4gh";

        [DataMember(Name = "artifact")]
        public string Artifact { get; set; } = "tes";

        [DataMember(Name = "version")]
        public string Version { get; set; } = "1.1.0";
    }

    [DataContract]
    public class TesServiceOrganization
    {
        [DataMember(Name = "name")]
        public string Name { get; set; } = string.Empty;

        [DataMember(Name = "url")]
        public string Url { get; set; } = string.Empty;
    }
}
