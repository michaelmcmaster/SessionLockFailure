using System;
using System.ComponentModel;
using System.Runtime.Serialization;
using Newtonsoft.Json;

namespace SessionLockFailure.Messages
{
    /// <example>
    /// {
    ///     "sessionNumber": 0
    ///     "text": "Some meaningful text",
    ///     "createdDate": "2024-01-01T00:00:00.0000000-00:00"
    /// }
    /// </example>
    [DataContract]
    [Description("Base-level data contract for Test messages")]
    [JsonObject(MemberSerialization.OptIn)]
    public class TestMessage
    {
        [DataMember]
        [Description("Session number")]
        [JsonProperty("sessionNumber", Required = Required.Always)]
        public int SessionNumber { get; set; }

        [DataMember]
        [Description("Text")]
        [JsonProperty("text", Required = Required.Always)]
        public string Text { get; set; } = string.Empty;

        [DataMember]
        [Description("Datetime when message was originally created")]
        [JsonProperty("createdDate", Required = Required.Always)]
        public DateTimeOffset CreatedDate { get; set; }


        // Constructor (for JSON deserializer)
        [JsonConstructor]
        public TestMessage()
        {
            // Nothing
        }

        // Constructor (for application w/all required fields)
        public TestMessage(int sessionNumber, string text)
        {
            // Properties
            SessionNumber = sessionNumber;
            Text = text;
            CreatedDate = DateTimeOffset.Now;
        }
    }
}
