﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.ServiceBus.Messaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ploeh.AutoFixture;
using Ploeh.AutoFixture.AutoMoq;
using Ploeh.AutoFixture.Idioms;

namespace Khala.Messaging.Azure
{
    [TestClass]
    public class EventHubMessageBus_features
    {
        public const string EventHubConnectionStringPropertyName = "eventhubmessagebus-eventhub-connectionstring";
        public const string EventHubPathPropertyName = "eventhubmessagebus-eventhub-path";
        public const string ConsumerGroupPropertyName = "eventhubmessagebus-eventhub-consumergroup";

        private static EventHubClient eventHubClient;
        private static string consumerGroupName;
        private IFixture fixture;
        private EventHubMessageBus sut;

        public TestContext TestContext { get; set; }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            var connectionString = (string)context.Properties[EventHubConnectionStringPropertyName];
            var path = (string)context.Properties[EventHubPathPropertyName];
            if (string.IsNullOrWhiteSpace(connectionString) == false &&
                string.IsNullOrWhiteSpace(path) == false)
            {
                eventHubClient = EventHubClient.CreateFromConnectionString(connectionString, path);
                consumerGroupName =
                    (string)context.Properties[ConsumerGroupPropertyName] ??
                    EventHubConsumerGroup.DefaultGroupName;
            }
        }

        [TestInitialize]
        public void TestInitialize()
        {
            if (eventHubClient == null)
            {
                Assert.Inconclusive($@"
Event Hub 연결 정보가 설정되지 않았습니다. EventHubMessageBus 클래스에 대한 테스트를 실행하려면 *.runsettings 파일에 다음과 같이 Event Hub 연결 정보를 설정합니다.

<?xml version=""1.0"" encoding=""utf-8"" ?>
<RunSettings>
  <TestRunParameters>
    <Parameter name=""{EventHubConnectionStringPropertyName}"" value=""your event hub connection string for testing"" />
    <Parameter name=""{EventHubPathPropertyName}"" value=""your event hub path for testing"" />
    <Parameter name=""{ConsumerGroupPropertyName}"" value=""[OPTIONAL] your event hub consumer group name for testing"" />
  </TestRunParameters>  
</RunSettings>

참고문서
- https://msdn.microsoft.com/en-us/library/jj635153.aspx
".Trim());
            }

            fixture = new Fixture().Customize(new AutoMoqCustomization());
            fixture.Inject(eventHubClient);
            sut = new EventHubMessageBus(eventHubClient, new JsonMessageSerializer());
        }

        [TestMethod]
        public void class_has_guard_clauses()
        {
            var assertion = new GuardClauseAssertion(fixture);
            assertion.Verify(typeof(EventHubMessageBus));
        }

        [TestMethod]
        public void SendBatch_has_guard_clause_for_null_envelope()
        {
            var envelopes = new Envelope[] { null };
            Func<Task> action = () => sut.SendBatch(envelopes);
            action.ShouldThrow<ArgumentException>()
                .Where(x => x.ParamName == "envelopes");
        }

        public class FooMessage : IPartitioned
        {
            public string SourceId { get; set; }

            public int Value { get; set; }

            string IPartitioned.PartitionKey => SourceId;
        }

        [TestMethod]
        public async Task Send_sends_message_correctly()
        {
            // Arrange
            var message = fixture.Create<FooMessage>();
            var correlationId = Guid.NewGuid();
            var envelope = new Envelope(correlationId, message);

            List<EventHubReceiver> receivers = await GetReceivers();

            try
            {
                // Act
                await sut.Send(envelope, CancellationToken.None);

                // Assert
                var waitTime = TimeSpan.FromSeconds(1);
                EventData eventData = null;
                foreach (EventHubReceiver receiver in receivers)
                {
                    eventData = await receiver.ReceiveAsync(waitTime);
                    if (eventData != null)
                    {
                        break;
                    }
                }

                eventData.Should().NotBeNull();
                Envelope actual = await sut.Serializer.Deserialize(eventData);
                actual.ShouldBeEquivalentTo(
                    envelope, opts => opts.RespectingRuntimeTypes());
            }
            finally
            {
                // Cleanup
                receivers.ForEach(r => r.Close());
            }
        }

        [TestMethod]
        public async Task SendBatch_sends_messages_correctly()
        {
            // Arrange
            var sourceId = fixture.Create<string>();
            List<Envelope> envelopes = fixture
                .Build<FooMessage>()
                .With(x => x.SourceId, sourceId)
                .CreateMany()
                .Select(m => new Envelope(m))
                .ToList();

            List<EventHubReceiver> receivers = await GetReceivers();

            try
            {
                // Act
                await sut.SendBatch(envelopes, CancellationToken.None);

                // Assert
                var waitTime = TimeSpan.FromSeconds(10);
                var eventDataList = new List<EventData>();
                foreach (EventHubReceiver receiver in receivers)
                {
                    IEnumerable<EventData> eventData = await
                        receiver.ReceiveAsync(envelopes.Count, waitTime);
                    if (eventData?.Any() ?? false)
                    {
                        eventDataList.AddRange(eventData);
                        break;
                    }
                }

                var actual = new List<Envelope>();
                foreach (EventData eventData in eventDataList)
                {
                    actual.Add(await sut.Serializer.Deserialize(eventData));
                }
                actual.ShouldAllBeEquivalentTo(
                    envelopes, opts => opts.RespectingRuntimeTypes());
            }
            finally
            {
                // Cleanup
                receivers.ForEach(r => r.Close());
            }
        }

        private async Task<List<EventHubReceiver>> GetReceivers()
        {
            EventHubConsumerGroup consumerGroup = eventHubClient.GetConsumerGroup(consumerGroupName);
            EventHubRuntimeInformation runtimeInfo = await eventHubClient.GetRuntimeInformationAsync();
            return await GetReceivers(consumerGroup, runtimeInfo);
        }

        private async Task<List<EventHubReceiver>> GetReceivers(
            EventHubConsumerGroup consumerGroup,
            EventHubRuntimeInformation runtimeInfo)
        {
            var receivers = new List<EventHubReceiver>();
            foreach (string partition in runtimeInfo.PartitionIds)
            {
                EventHubReceiver receiver = await
                    consumerGroup.CreateReceiverAsync(
                        partition,
                        EventHubConsumerGroup.EndOfStream);
                receivers.Add(receiver);
            }

            return receivers;
        }
    }
}
