﻿namespace Khala.Messaging.Azure
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using FluentAssertions;
    using Khala.TransientFaultHandling;
    using Microsoft.Azure.EventHubs;
    using Microsoft.Azure.EventHubs.Processor;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Moq;
    using Newtonsoft.Json;
    using Ploeh.AutoFixture;
    using Ploeh.AutoFixture.AutoMoq;
    using Ploeh.AutoFixture.Idioms;

    [TestClass]
    public class EventProcessorFactory_specs
    {
        public const string EventHubConnectionStringParam = "EventProcessorFactory/EventHubConnectionString";
        public const string ConsumerGroupNameParam = "EventProcessorFactory/ConsumerGroupName";
        public const string StorageConnectionStringParam = "EventProcessorFactory/StorageConnectionString";
        public const string LeaseContainerNameParam = "EventProcessorFactory/LeaseContainerName";

        private static readonly string ConnectionParametersRequired = $@"Event Hub connection information is not set. To run tests on the EventProcessorFactory class, you must set the connection information in the *.runsettings file as follows:

<?xml version=""1.0"" encoding=""utf-8"" ?>
<RunSettings>
  <TestRunParameters>
    <Parameter name=""{EventHubConnectionStringParam}"" value=""your connection string to the Event Hub"" />
    <Parameter name=""{ConsumerGroupNameParam}"" value=""[OPTIONAL] The name of the consumer group within the Event Hub"" />
    <Parameter name=""{StorageConnectionStringParam}"" value=""your connection string to Storage account for leases and checkpointing"" />
    <Parameter name=""{LeaseContainerNameParam}"" value=""Azure Storage container name for leases and checkpointing"" />
  </TestRunParameters>  
</RunSettings>

References
- https://msdn.microsoft.com/en-us/library/jj635153.aspx";

        private static string _eventHubConnectionString;
        private static string _consumerGroupName;
        private static string _storageConnectionString;
        private static string _leaseContainerName;

        public TestContext TestContext { get; set; }

        private static EventProcessorHost GetEventProcessorHost()
        {
            return new EventProcessorHost(
                eventHubPath: null,
                consumerGroupName: _consumerGroupName,
                eventHubConnectionString: _eventHubConnectionString,
                storageConnectionString: _storageConnectionString,
                leaseContainerName: _leaseContainerName);
        }

        private static async Task<EventHubRuntimeInformation> GetRuntimeInformation()
        {
            var eventHubClient = EventHubClient.CreateFromConnectionString(_eventHubConnectionString);
            EventHubRuntimeInformation runtimeInformation = await eventHubClient.GetRuntimeInformationAsync();
            await eventHubClient.CloseAsync();
            return runtimeInformation;
        }

        private static async Task CheckpointLatest(EventHubRuntimeInformation runtimeInformation)
        {
            var leasedPartitionIds = new HashSet<string>();
            var stopwatch = new Stopwatch();

            EventProcessorHost processorHost = GetEventProcessorHost();

            var factory = new CheckpointerFactory(() => new Checkpointer
            {
                OnOpen = partitionContext => leasedPartitionIds.Add(partitionContext.PartitionId),
                OnCheckpoint = eventData => stopwatch.Restart()
            });
            await processorHost.RegisterEventProcessorFactoryAsync(factory);

            do
            {
                await Task.Delay(10);
            }
            while (leasedPartitionIds.Count < runtimeInformation.PartitionCount);

            stopwatch.Start();
            do
            {
                await Task.Delay(10);
            }
            while (stopwatch.Elapsed.TotalSeconds < 1.0);
            stopwatch.Stop();

            await processorHost.UnregisterEventProcessorAsync();
        }

        private class Checkpointer : IEventProcessor
        {
            public Action<PartitionContext> OnOpen { get; set; }

            public Action<EventData> OnCheckpoint { get; set; }

            public Task CloseAsync(PartitionContext context, CloseReason reason) => Task.CompletedTask;

            public Task OpenAsync(PartitionContext context)
            {
                OnOpen(context);
                return Task.CompletedTask;
            }

            public Task ProcessErrorAsync(PartitionContext context, Exception error) => Task.CompletedTask;

            public async Task ProcessEventsAsync(PartitionContext context, IEnumerable<EventData> messages)
            {
                foreach (EventData eventData in messages)
                {
                    await context.CheckpointAsync(eventData);
                    OnCheckpoint?.Invoke(eventData);
                }
            }
        }

        private class CheckpointerFactory : IEventProcessorFactory
        {
            private readonly Func<Checkpointer> _function;

            public CheckpointerFactory(Func<Checkpointer> function) => _function = function;

            public IEventProcessor CreateEventProcessor(PartitionContext context) => _function.Invoke();
        }

        public class PartitionLease
        {
            public string PartitionId { get; set; }

            public string Offset { get; set; }

            public int SequenceNumber { get; set; }

            public string Owner { get; set; }

            public string Token { get; set; }

            public int Epoch { get; set; }
        }

        public static async Task<PartitionLease> GetPartitionLease(string partitionId)
        {
            var storageAccount = CloudStorageAccount.Parse(_storageConnectionString);
            CloudBlobClient blobClient = storageAccount.CreateCloudBlobClient();
            CloudBlobContainer blobContainer = blobClient.GetContainerReference(_leaseContainerName);
            ICloudBlob blob = await blobContainer.GetBlobReferenceFromServerAsync($"{_consumerGroupName}/{partitionId}");
            using (Stream stream = await blob.OpenReadAsync())
            using (var reader = new StreamReader(stream))
            {
                string content = await reader.ReadToEndAsync();
                return JsonConvert.DeserializeObject<PartitionLease>(content);
            }
        }

        private static async Task<IReadOnlyCollection<PartitionLease>> GetPartitionLeases(EventHubRuntimeInformation runtimeInformation)
        {
            return new List<PartitionLease>(await Task.WhenAll(runtimeInformation.PartitionIds.Select(GetPartitionLease)));
        }

        [ClassInitialize]
        public static void ClassInitialize(TestContext context)
        {
            _eventHubConnectionString = (string)context.Properties[EventHubConnectionStringParam];
            _storageConnectionString = (string)context.Properties[StorageConnectionStringParam];
            _leaseContainerName = (string)context.Properties[LeaseContainerNameParam];

            if (string.IsNullOrWhiteSpace(_eventHubConnectionString) ||
                string.IsNullOrWhiteSpace(_storageConnectionString) ||
                string.IsNullOrWhiteSpace(_leaseContainerName))
            {
                Assert.Inconclusive(ConnectionParametersRequired);
            }

            _consumerGroupName = (string)context.Properties[ConsumerGroupNameParam] ?? PartitionReceiver.DefaultConsumerGroupName;
        }

        [TestMethod]
        public void sut_has_guard_clauses()
        {
            var builder = new Fixture();
            builder.Customize(new AutoMoqCustomization());
            builder.Register<PartitionContext>(() => null);
            new GuardClauseAssertion(builder).Verify(typeof(EventProcessorFactory));
        }

        [TestMethod]
        public async Task event_processor_invokes_message_handler_correctly()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            var messageHandler = new MessageHandler();
            var sut = new EventProcessorFactory(
                messageHandler,
                Mock.Of<IEventProcessingExceptionHandler>(),
                CancellationToken.None);

            var messageBus = new EventHubMessageBus(
                EventHubClient.CreateFromConnectionString(_eventHubConnectionString));

            var envelope = new Envelope(new Fixture().Create<Message>());

            // Act
            await messageBus.Send(envelope);

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            await RetryPolicy<bool>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(() => Task.FromResult(messageHandler.Handled.Any()));

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            messageHandler.Handled.Should().ContainSingle();
            Envelope actual = messageHandler.Handled.Single();
            actual.ShouldBeEquivalentTo(envelope);
        }

        [TestMethod]
        public async Task event_processor_checkpoints_correctly()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var messageHandler = new MessageHandler();
            var sut = new EventProcessorFactory(
                messageHandler,
                Mock.Of<IEventProcessingExceptionHandler>(),
                CancellationToken.None);

            var messageBus = new EventHubMessageBus(
                EventHubClient.CreateFromConnectionString(_eventHubConnectionString));

            int messageCount = 5;

            // Act
            for (int i = 0; i < messageCount; i++)
            {
                var message = new Fixture().Create<Message>();
                await messageBus.Send(new Envelope(message));
            }

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            await RetryPolicy<bool>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(() => Task.FromResult(messageHandler.Handled.Count >= messageCount));

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            IEnumerable<int> diffs = from first in partitionLeases
                                     join second in await GetPartitionLeases(runtimeInformation)
                                     on first.PartitionId equals second.PartitionId
                                     select second.SequenceNumber - first.SequenceNumber;
            diffs.Sum().Should().Be(messageCount);
        }

        [TestMethod]
        public async Task event_processor_checkpoints_after_message_handled()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var completion = new TaskCompletionSource<bool>();
            var messageHandler = Mock.Of<IMessageHandler>();
            var handled = false;
            Mock.Get(messageHandler)
                .Setup(x => x.Handle(It.IsAny<Envelope>(), CancellationToken.None))
                .Callback(() => handled = true)
                .Returns(completion.Task);

            var sut = new EventProcessorFactory(
                messageHandler,
                Mock.Of<IEventProcessingExceptionHandler>(),
                CancellationToken.None);

            var messageBus = new EventHubMessageBus(
                EventHubClient.CreateFromConnectionString(_eventHubConnectionString));

            // Act
            var message = new Fixture().Create<Message>();
            await messageBus.Send(new Envelope(message));

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            await RetryPolicy<bool>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(() => Task.FromResult(handled));

            // Assert
            try
            {
                IEnumerable<string> changedPartition =
                    from first in partitionLeases
                    join second in await GetPartitionLeases(runtimeInformation)
                    on first.PartitionId equals second.PartitionId
                    where first.SequenceNumber != second.SequenceNumber
                    select first.PartitionId;
                changedPartition.Should().BeEmpty();
            }
            finally
            {
                completion.SetResult(true);
                await processorHost.UnregisterEventProcessorAsync();
            }
        }

        [TestMethod]
        public async Task event_processor_checkpoints_even_if_serializer_fails()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var sut = new EventProcessorFactory(
                Mock.Of<IMessageHandler>(),
                Mock.Of<IEventProcessingExceptionHandler>(),
                CancellationToken.None);

            // Act
            var eventHubClient = EventHubClient.CreateFromConnectionString(_eventHubConnectionString);
            byte[] badBytes = new byte[] { 0x0000_0000, 0x0000_0001 };
            await eventHubClient.SendAsync(new EventData(badBytes));

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            int checkpointDifference = await RetryPolicy<int>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(async () =>
                {
                    IEnumerable<int> diffs = from first in partitionLeases
                                             join second in await GetPartitionLeases(runtimeInformation)
                                             on first.PartitionId equals second.PartitionId
                                             select second.SequenceNumber - first.SequenceNumber;
                    return diffs.Sum();
                });

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            checkpointDifference.Should().Be(1);
        }

        [TestMethod]
        public async Task event_processor_checkpoints_even_if_message_handler_fails()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var messageHandler = Mock.Of<IMessageHandler>();
            Mock.Get(messageHandler)
                .Setup(x => x.Handle(It.IsAny<Envelope>(), CancellationToken.None))
                .ThrowsAsync(new InvalidOperationException());

            var sut = new EventProcessorFactory(
                messageHandler,
                Mock.Of<IEventProcessingExceptionHandler>(),
                CancellationToken.None);

            var messageBus = new EventHubMessageBus(
                EventHubClient.CreateFromConnectionString(_eventHubConnectionString));

            // Act
            var message = new Fixture().Create<Message>();
            await messageBus.Send(new Envelope(message));

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            int checkpointDifference = await RetryPolicy<int>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(async () =>
                {
                    IEnumerable<int> diffs = from first in partitionLeases
                                             join second in await GetPartitionLeases(runtimeInformation)
                                             on first.PartitionId equals second.PartitionId
                                             select second.SequenceNumber - first.SequenceNumber;
                    return diffs.Sum();
                });

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            checkpointDifference.Should().Be(1);
        }

        [TestMethod]
        public async Task event_processor_invokes_exception_handler_for_serializer_exception()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            EventProcessingExceptionContext exceptionContext = null;
            var exceptionHandler = Mock.Of<IEventProcessingExceptionHandler>();
            Mock.Get(exceptionHandler)
                .Setup(x => x.Handle(It.IsAny<EventProcessingExceptionContext>()))
                .Callback<EventProcessingExceptionContext>(p => exceptionContext = p)
                .Returns(Task.CompletedTask);

            var sut = new EventProcessorFactory(
                Mock.Of<IMessageHandler>(),
                exceptionHandler,
                CancellationToken.None);

            // Act
            var eventHubClient = EventHubClient.CreateFromConnectionString(_eventHubConnectionString);
            byte[] badBytes = new byte[] { 0x0000_0000, 0x0000_0001 };
            await eventHubClient.SendAsync(new EventData(badBytes));

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            await RetryPolicy<bool>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(async () =>
                {
                    IEnumerable<int> diffs = from first in partitionLeases
                                             join second in await GetPartitionLeases(runtimeInformation)
                                             on first.PartitionId equals second.PartitionId
                                             select second.SequenceNumber - first.SequenceNumber;
                    return diffs.Sum() > 0;
                });

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            Mock.Get(exceptionHandler).Verify(x => x.Handle(It.IsAny<EventProcessingExceptionContext>()), Times.Once());
            exceptionContext.EventData.Should().NotBeNull();
            exceptionContext.EventData.Body.ToArray().Should().Equal(badBytes);
            exceptionContext.Envelope.Should().BeNull();
            exceptionContext.Exception.Should().BeOfType<JsonReaderException>();
        }

        [TestMethod]
        public async Task event_processor_invokes_exception_handler_for_message_handler_exception()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var messageHandler = Mock.Of<IMessageHandler>();
            Exception exception = new InvalidOperationException();
            Mock.Get(messageHandler)
                .Setup(x => x.Handle(It.IsAny<Envelope>(), CancellationToken.None))
                .ThrowsAsync(exception);

            EventProcessingExceptionContext exceptionContext = null;
            var exceptionHandler = Mock.Of<IEventProcessingExceptionHandler>();
            Mock.Get(exceptionHandler)
                .Setup(x => x.Handle(It.IsAny<EventProcessingExceptionContext>()))
                .Callback<EventProcessingExceptionContext>(p => exceptionContext = p)
                .Returns(Task.CompletedTask);

            var sut = new EventProcessorFactory(
                messageHandler,
                exceptionHandler,
                CancellationToken.None);

            var messageBus = new EventHubMessageBus(
                EventHubClient.CreateFromConnectionString(_eventHubConnectionString));

            var envelope = new Envelope(new Fixture().Create<Message>());

            // Act
            await messageBus.Send(envelope);

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            await RetryPolicy<bool>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(async () =>
                {
                    IEnumerable<int> diffs = from first in partitionLeases
                                             join second in await GetPartitionLeases(runtimeInformation)
                                             on first.PartitionId equals second.PartitionId
                                             select second.SequenceNumber - first.SequenceNumber;
                    return diffs.Sum() > 0;
                });

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            Mock.Get(exceptionHandler).Verify(x => x.Handle(It.IsAny<EventProcessingExceptionContext>()), Times.Once());
            exceptionContext.EventData.Should().NotBeNull();
            exceptionContext.EventData.Body.ShouldBeEquivalentTo(new EventDataSerializer().Serialize(envelope).Body);
            exceptionContext.Envelope.ShouldBeEquivalentTo(envelope);
            exceptionContext.Exception.Should().BeSameAs(exception);
        }

        [TestMethod]
        public async Task event_processor_checkpoints_even_if_exception_handler_fails()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var exceptionHandler = Mock.Of<IEventProcessingExceptionHandler>();
            Mock.Get(exceptionHandler)
                .Setup(x => x.Handle(It.IsAny<EventProcessingExceptionContext>()))
                .ThrowsAsync(new InvalidOperationException());

            var sut = new EventProcessorFactory(
                Mock.Of<IMessageHandler>(),
                exceptionHandler,
                CancellationToken.None);

            // Act
            var eventHubClient = EventHubClient.CreateFromConnectionString(_eventHubConnectionString);
            byte[] badBytes = new byte[] { 0x0000_0000, 0x0000_0001 };
            await eventHubClient.SendAsync(new EventData(badBytes));

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            int checkpointDifference = await RetryPolicy<int>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(async () =>
                {
                    IEnumerable<int> diffs = from first in partitionLeases
                                             join second in await GetPartitionLeases(runtimeInformation)
                                             on first.PartitionId equals second.PartitionId
                                             select second.SequenceNumber - first.SequenceNumber;
                    return diffs.Sum();
                });

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            checkpointDifference.Should().Be(1);
        }

        [TestMethod]
        public async Task event_processor_does_not_checkpoint_if_TaskCanceledException_occurs()
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var messageHandler = Mock.Of<IMessageHandler>();
            Mock.Get(messageHandler)
                .Setup(x => x.Handle(It.IsAny<Envelope>(), CancellationToken.None))
                .ThrowsAsync(new TaskCanceledException());

            var sut = new EventProcessorFactory(
                messageHandler,
                Mock.Of<IEventProcessingExceptionHandler>(),
                CancellationToken.None);

            var messageBus = new EventHubMessageBus(
                EventHubClient.CreateFromConnectionString(_eventHubConnectionString));

            // Act
            await messageBus.Send(new Envelope(new Fixture().Create<Message>()));

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            await TransientFaultHandling.RetryPolicy
                .Linear(5, TimeSpan.FromMilliseconds(500))
                .Run(() =>
                {
                    Mock.Get(messageHandler).Verify(x => x.Handle(It.IsAny<Envelope>(), CancellationToken.None));
                    return Task.CompletedTask;
                });

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            IEnumerable<int> diffs = from first in partitionLeases
                                     join second in await GetPartitionLeases(runtimeInformation)
                                     on first.PartitionId equals second.PartitionId
                                     select second.SequenceNumber - first.SequenceNumber;
            diffs.Sum().Should().Be(0);
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task event_processor_invokes_message_handler_with_cancellation_token(bool canceled)
        {
            // Arrange
            EventHubRuntimeInformation runtimeInformation = await GetRuntimeInformation();
            await CheckpointLatest(runtimeInformation);

            IReadOnlyCollection<PartitionLease> partitionLeases = await GetPartitionLeases(runtimeInformation);

            var messageHandler = Mock.Of<IMessageHandler>();
            var cancellationToken = new CancellationToken(canceled);
            var sut = new EventProcessorFactory(
                messageHandler,
                Mock.Of<IEventProcessingExceptionHandler>(),
                cancellationToken);

            var messageBus = new EventHubMessageBus(
                EventHubClient.CreateFromConnectionString(_eventHubConnectionString));

            // Act
            await messageBus.Send(new Envelope(new Fixture().Create<Message>()));

            EventProcessorHost processorHost = GetEventProcessorHost();
            await processorHost.RegisterEventProcessorFactoryAsync(sut);

            await RetryPolicy<int>
                .LinearTransientDefault(5, TimeSpan.FromMilliseconds(500))
                .Run(async () =>
                {
                    IEnumerable<int> diffs = from first in partitionLeases
                                             join second in await GetPartitionLeases(runtimeInformation)
                                             on first.PartitionId equals second.PartitionId
                                             select second.SequenceNumber - first.SequenceNumber;
                    return diffs.Sum();
                });

            await processorHost.UnregisterEventProcessorAsync();

            // Assert
            Mock.Get(messageHandler).Verify(x => x.Handle(It.IsAny<Envelope>(), cancellationToken), Times.Once());
        }

        public class MessageHandler : IMessageHandler
        {
            private readonly ConcurrentQueue<Envelope> _handled;

            public MessageHandler() => _handled = new ConcurrentQueue<Envelope>();

            public IReadOnlyCollection<Envelope> Handled => _handled;

            public Task Handle(Envelope envelope, CancellationToken cancellationToken)
            {
                _handled.Enqueue(envelope);
                return Task.CompletedTask;
            }
        }

        public class Message
        {
            public int Sequence { get; set; }

            public string Content { get; set; }
        }
    }
}