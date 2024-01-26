using System;
using System.Text;
using System.Globalization;
using Azure.Messaging.ServiceBus;
using Newtonsoft.Json;

namespace SessionLockFailure.Messages
{
    public static class TestMessageExtensions
    {
        // TestMessage To JSON
        public static string ToJson<T>(this T testMessage) where T : TestMessage
        {
            return JsonConvert.SerializeObject(testMessage);
        }

        // JSON to TestMessage
        public static T? ToTestMessage<T>(string jsonString) where T : TestMessage
        {
            return JsonConvert.DeserializeObject<T>(jsonString);
        }

        // ServiceBusMessage to TestMessage
        public static T? ToTestMessage<T>(this ServiceBusReceivedMessage serviceBusReceivedMessage) where T : TestMessage
        {
            return ToTestMessage<T>(serviceBusReceivedMessage.Body.ToString());
        }

        // TestMessage to ServiceBusMessage
        public static ServiceBusMessage ToServiceBusMessage<T>(this T workMessage, string? correlationId) where T : TestMessage
        {
            var workMessageJson = workMessage.ToJson();
            return workMessage.ToServiceBusMessage(workMessageJson, correlationId);
        }

        private static ServiceBusMessage ToServiceBusMessage(this TestMessage testMessage, string testMessageJson, string? correlationId)
        {
            var serviceBusMessage = new ServiceBusMessage()
            {
                Body = new BinaryData(Encoding.UTF8.GetBytes(testMessageJson)),
                ContentType = "application/json;charset=utf-8",
                Subject = "Test",
                CorrelationId = correlationId,
                SessionId = testMessage.SessionNumber.ToString("D", CultureInfo.InvariantCulture)
            };

            return serviceBusMessage;
        }
    }
}
