using Newtonsoft.Json;

namespace OBSWebsocket.NETStandard.Types
{
    /// <summary>
    /// Streaming settings
    /// </summary>
    public class StreamingService
    {
        /// <summary>
        /// Type of streaming service
        /// </summary>
        [JsonProperty(PropertyName = "type")]
        public string Type { set; get; }

        /// <summary>
        /// Streaming service settings (JSON data)
        /// </summary>
        [JsonProperty(PropertyName = "source")]
        public StreamingServiceSettings Settings { set; get; }
    }
}