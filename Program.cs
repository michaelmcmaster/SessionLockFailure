using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using CommandLine;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;
using SessionLockFailure.Messages;

namespace SessionLockFailure
{
    internal class Program
    {        
        public class Options
        {
            [Option('c', "connection", Required = true, HelpText = "Service Bus connection string (Manage, Send, Listen)")]
            public string ConnectionString { get; set; } = string.Empty;

            [Option('m', "messages", Required = false, HelpText = "Number of messages to put into Service Bus (single session)", Default = 1)]
            public int MessageCount { get; set; }

            [Option('p', "prefetch", Required = false, HelpText = "Service Bus receive prefetch size", Default = 2)]
            public int PrefetchSize { get; set; }

            [Option('q', "queue", Required = false, HelpText = "Service Bus queue name", Default = "session_lock_failure")]
            public string QueueName { get; set; } = string.Empty;
        }

        static Program()
        {
            // Install the UnhandledException handler
            AppDomain currentDomain = AppDomain.CurrentDomain;
            currentDomain.UnhandledException += new UnhandledExceptionEventHandler((s, e) =>
            {
                var exception = e.ExceptionObject as Exception;

                if (Log.Logger != null)
                {
                    Log.Logger.Fatal(exception, "{Exception}", exception?.Message);
                    Log.Logger.Information("Program exiting (failure)");
                    Log.CloseAndFlush();
                }
                else
                {
                    Console.Error.WriteLine(exception);
                    Console.WriteLine("Program exiting (failure)");
                }

                Environment.Exit(1);
            });

            // Configure Serilog global logger
            Log.Logger = new LoggerConfiguration()
                .Enrich.FromLogContext()
                .Enrich.WithThreadId()
                .MinimumLevel.Verbose()
                .WriteTo.File("SessionLockFailure-.log",
                    rollingInterval: RollingInterval.Day,
                    restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Verbose,
                    outputTemplate: "{Timestamp:O} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}",
                    buffered: false)
                .WriteTo.Console(
                    theme: AnsiConsoleTheme.Code,
                    restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
                    outputTemplate: "{Timestamp:O} [{Level:u3}] [{ThreadId}] {Message:lj}{NewLine}{Exception}")
                .CreateLogger();

            new Microsoft.Extensions.Logging.LoggerFactory().AddSerilog();

            // Add Serilog listener to the Azure SDK logging system
            Trace.Listeners.Add(new SerilogTraceListener.SerilogTraceListener());
            Azure.Core.Diagnostics.AzureEventSourceListener.CreateTraceLogger(System.Diagnostics.Tracing.EventLevel.Verbose);
        }

        static async Task Main(string[] args)
        {
            Log.Information("SessionLockFailure running");
            var options = Parser.Default.ParseArguments<Options>(args).Value;
            if (options == null) return; // Help, Version (no options) early exit
            if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new ArgumentException("Invalid connection string", nameof(options.ConnectionString));
            if (options.PrefetchSize <= 0) throw new ArgumentException("Prefetch must be greater than zero", nameof(options.PrefetchSize));
            if (options.MessageCount <= 0) throw new ArgumentException("Messages must be greater than zero", nameof(options.MessageCount));

            // Trash the queue and recreate (reset to known state)
            await BootstrapFreshQueue(options);

            // Add messages to the queue 
            await using var serviceBusClient = new ServiceBusClient(options.ConnectionString);
            await SendMessages(options, serviceBusClient, options.MessageCount);

            Log.Debug("AcceptNextSession: [{Name}]", options.QueueName);
            await using var sessionReceiver = await serviceBusClient.AcceptNextSessionAsync(options.QueueName,
                new ServiceBusSessionReceiverOptions() { PrefetchCount = options.PrefetchSize });

            if (sessionReceiver != null)
            {
                var wasSessionLockLost = false;

                Log.Information("AcceptNextSession: SessionId:[{SessionId}], LockedUntil:[{LockedUntil:O}]",
                    sessionReceiver.SessionId, sessionReceiver.SessionLockedUntil.ToLocalTime());

                // NOTE: This operation will fetch (into local memory) up to PREFETCH_SIZE number of messages
                var receivedMessage = await sessionReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));

                // Wait until the SessionLock (provided by ServiceBus) has... or should have... expired
                // NOTE: This simulates a long-running (or malfunctioning) message handler
                var delaySpan = sessionReceiver.SessionLockedUntil - DateTimeOffset.Now;
                Log.Information("Delay:[{delaySpan}] to allow session lock to expire", delaySpan);
                await Task.Delay(delaySpan);

                // Give some additional time after the SessionLock *should* have expired
                // - Accommodate a little SNTP drift
                // - Give an opportunity to attempt a Receive from another client to test if the SessionLock has *actually* expired
                //
                // NOTE: This can be made VERY large to simulate a "stuck application", during which the (server-side) session lock
                //       is being *maintained* by the client connection... causing the session being "indefinitely stalled".
                //       This can be confirmed by attempting a AcquireNextSession + Receive (from another client) during this delay.
                delaySpan = TimeSpan.FromMinutes(1);
                Log.Information("Delay:[{delaySpan}] for extra measure", delaySpan);
                await Task.Delay(delaySpan);

                try
                {
                    // LOOP: Receive messages within the session until none are available
                    while (receivedMessage != null)
                    {
                        Log.Information("CompleteMessage: SessionId:[{SessionId}], SequenceNumber:[{SequenceNumber}]",
                            receivedMessage.SessionId, receivedMessage.SequenceNumber);

                        // NOTE: By this time, the session *should* have expired and the CompleteMessage operation *should* fail
                        await sessionReceiver.CompleteMessageAsync(receivedMessage);

                        // NOTE: This operation will likely fetch from local memory (depends on PREFETCH_SIZE)
                        receivedMessage = await sessionReceiver.ReceiveMessageAsync(TimeSpan.FromSeconds(1));
                    }
                }
                catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionLockLost)
                {
                    Log.Warning(ex, "CompleteMessage: Session lock lost (expected)");
                    wasSessionLockLost = true;
                }

                // The above "receive + delay + complete" should have resulted in a SessionLockLost condition
                if (wasSessionLockLost == false)
                {
                    Log.Error("FAILURE: The sesion lock *should* have been lost, but was not");
                }
            }
        }

        static async Task BootstrapFreshQueue(Options options)
        {
            var adminClient = new ServiceBusAdministrationClient(options.ConnectionString);
            
            // STEP: Delete the Queue if it exists
            try
            {
                await adminClient.GetQueueAsync(options.QueueName);

                Log.Debug("Deleting queue:[{QueueName}]", options.QueueName);
                using var deleteResponse = await adminClient.DeleteQueueAsync(options.QueueName);

                if ((deleteResponse == null) ||
                    (deleteResponse.IsError))
                {
                    Log.Error("Failed to remove queue:[{QueueName}]: Reason:[{ReasonPhrase}]",
                        options.QueueName, deleteResponse?.ReasonPhrase);
                }
                else
                {
                    Log.Debug("Deleted queue:[{QueueName}]", options.QueueName);
                }
            }
            catch (ServiceBusException e) when (e.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
                // Queue doesn't exist
            }

            // STEP: Create the Queue (requires session)
            Log.Debug("Creating queue:[{QueueName}]", options.QueueName);
            var createResponse = await adminClient.CreateQueueAsync(new CreateQueueOptions(options.QueueName)
            {
                DefaultMessageTimeToLive = TimeSpan.FromDays(7),
                LockDuration = TimeSpan.FromSeconds(15),
                MaxSizeInMegabytes = 1024, // Smallest supported option
                RequiresSession = true
            });

            if (createResponse != null)
            {
                Log.Debug("Created queue:[{QueueName}]",
                    createResponse.Value.Name);
            }
            else
            {
                Log.Error("Empty response from CreateQueueAsync");
            }
        }

        static async Task SendMessages(Options options, ServiceBusClient serviceBusClient, int messagesToSend)
        {
            // NOTE: Batch size is limited (by the ServiceBus) to 100 messages per transaction
            // https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-quotas#messaging-quotas
            // See: "Number of messages per transaction"
            const int BatchSizeMax = 100;

            var messageBatchList = new List<ServiceBusMessage>();
            var correlationIdString = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);
            var stopwatch = Stopwatch.StartNew();
            var totalBatchCount = 0;
            var totalMessageCount = 0;

            await using var sender = serviceBusClient.CreateSender(options.QueueName);
            Log.Information("ServiceBus client connected");
            Log.Debug("Generating [{messagesToSend}] with correlationId:[{correlationId}]",
                messagesToSend, correlationIdString);

            while (totalMessageCount < messagesToSend)
            {
                // Determine how the messages will be distributed into session(s)
                var sessionNumber =
                    0;                          // All in a single session
                    //totalMessageCount % 10;   // Evenly across 10 sessions
                    //totalMessageCount;        // Each message in its own session

                var testMessage = new TestMessage(sessionNumber, totalMessageCount.ToString("D", CultureInfo.InvariantCulture));
                messageBatchList.Add(testMessage.ToServiceBusMessage(correlationIdString));

                ++totalMessageCount;

                if (messageBatchList.Count >= BatchSizeMax)
                {
                    ++totalBatchCount;
                    Log.Information("Sending batch: [{totalBatchCount}]", totalBatchCount);
                    await sender.SendMessagesAsync(messageBatchList);

                    // Make new List for next batch
                    messageBatchList = [];
                }
            }

            // Send the final "partial" batch, if any
            if (messageBatchList.Count > 0)
            {
                ++totalBatchCount;
                Log.Information("Sending partial batch: [{totalBatchCount}]", totalBatchCount);
                await sender.SendMessagesAsync(messageBatchList);
            }

            // Performance summary
            var totalSeconds = stopwatch.Elapsed.TotalSeconds;
            var messagesPerSecond = totalMessageCount / stopwatch.Elapsed.TotalSeconds;
            Log.Information("Sent [{totalMessageCount}] messages in [{totalSeconds:N2}] seconds ({messagesPerSecond:N2} msg/s).",
                totalMessageCount, totalSeconds, messagesPerSecond);
        }
    }
}
