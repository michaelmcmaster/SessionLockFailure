## Issue Description
If the Service Bus receiver uses a `PrefetchSize` that is larger than the number of messages in the Service Bus session, the session lock seems to be automatically renewed until the client connection is closed.  It does **not** matter if the session is from a Queue or a Subscription - the failure mode exists with both.

I cannot determine if the session lock is being automatically renewed by the client or by something server-side.  I don't see any activity in the client logs that indicates the client (automatically) renewing the session lock.  The session lock *is* released (on the server) if the connection between the client and the server is severed.

## Recreate
I posted a Visual Studio 2022 solution (console application) that recreates the issue to [GitHub](https://github.com/michaelmcmaster/SessionLockFailure.git).  Informational logs are written to the console, while trace logs are written to a file in the working directory.

This application can be pointed to a ServiceBus, and it will:
* Bootstrap (create or delete and recreate) a fresh queue (default name of 'session_lock_failure')
  - `RequiresSession` set to `true`
  - `LockDuration` set to 15 seconds (intentionally short for quicker testing, but the issue recreates with any time span)
* Send a configurable number messages to the queue
  - all messages have the same session identifier
* Using the Service Bus client, performs an `AcceptNextSession` operation
  - `ReceiverOptions` set to a configurable `PrefetchSize`
* Using the `sessionReceiver` from ^^^, performs an `ReceiveMessage` operation
* Simulates a "slow receiver" by delaying until the `sessionReceiver`'s `SessionLockedUntil` is reached
* Delays for an *additional* amount of time (amount of additional delay doesn't seem to matter)
* Using the `sessionReceiver` and `receivedMessage` from ^^^, performs a `CompleteMessage` operation

**ISSUE**: The `CompleteMessage` should *always* result in a `SessionLockLost` exception, but if the `PrefetchSize` is larger than the number of messages in the session, the message(s) are succesfully completed (and removed from the queue).

### Command Line Options
    -c, --connection    Required. Service Bus connection string (Manage, Send, Listen)
    -m, --messages      (Default: 1) Number of messages to put into Service Bus (single session)
    -p, --prefetch      (Default: 2) Service Bus receive prefetch size
    -q, --queue         (Default: session_lock_failure) Service Bus queue name

## Scenario 1 (OK) : messages >= prefetch
With this scenario, the Service Bus behaves according to official documentation.  During the delay, the server-side expires the session lock and a `SessionLockLost` exception is thrown when the client-side attempts (after the delay) to complete the messages.

Command Line: `SessionLockFailure.exe -c "******" -m 2 -p 2`

    2024-01-26T15:34:45.8599208-06:00 [INF] [1] SessionLockFailure running
    2024-01-26T15:34:47.8922086-06:00 [INF] [10] ServiceBus client connected
    2024-01-26T15:34:47.9612183-06:00 [INF] [10] Sending partial batch: [1]
    2024-01-26T15:34:48.4635329-06:00 [INF] [5] Sent [2] messages in [0.57] seconds (3.49 msg/s).
    2024-01-26T15:34:48.5755560-06:00 [INF] [10] AcceptNextSession: SessionId:[0], LockedUntil:[2024-01-26T15:35:03.5221389-06:00]
    2024-01-26T15:34:48.6002566-06:00 [INF] [7] Delay:[00:00:14.9220584] to allow session lock to expire
    2024-01-26T15:35:03.5301752-06:00 [INF] [7] Delay:[00:05:00] for extra measure
    2024-01-26T15:40:03.5212820-06:00 [INF] [34] CompleteMessage: SessionId:[0], SequenceNumber:[1]
    2024-01-26T15:40:03.5328858-06:00 [WRN] [34] CompleteMessage: Session lock lost (expected)
    Azure.Messaging.ServiceBus.ServiceBusException: The session lock has expired on the MessageSession. Accept a new MessageSession. TrackingId:*****, SystemTracker:***:***:amqps://******/***;0:7:8:source(address:/session_lock_failure,filter:[com.microsoft:session-filter:]), Timestamp:2024-01-26T21:35:03 (SessionLockLost).

## Scenario 2 (Failure) : messages < prefetch
With this scenario, the Service Bus misbehaves (session lock is held indefinitely).  During the delay, the server-side does *not* expire the session lock.  The (server-side) session lock is being *indefinitely maintained* by the client connection... causing the session to be *indefinitely* stalled until the client connection is terminated.  This can be further confirmed by attempting a AcquireNextSession + Receive (ex. from ServiceBusExplorer) during the delay period.  Messages are successfully completed when the client-side attempts (after the delay) to complete the messages.

Command Line: `SessionLockFailure.exe -c "*****" -m 1 -p 2`

    2024-01-26T14:48:22.8077862-06:00 [INF] [1] SessionLockFailure running
    2024-01-26T14:48:25.0585689-06:00 [INF] [10] ServiceBus client connected
    2024-01-26T14:48:25.1456060-06:00 [INF] [10] Sending partial batch: [1]
    2024-01-26T14:48:25.7762166-06:00 [INF] [10] Sent [1] messages in [0.72] seconds (1.39 msg/s).
    2024-01-26T14:48:25.8877994-06:00 [INF] [10] AcceptNextSession: SessionId:[0], LockedUntil:[2024-01-26T14:48:40.7684016-06:00]
    2024-01-26T14:48:25.9198234-06:00 [INF] [10] Delay:[00:00:14.8487970] to allow session lock to expire
    2024-01-26T14:48:40.7726083-06:00 [INF] [10] Delay:[00:30:00] for extra measure
    2024-01-26T15:18:40.7755738-06:00 [INF] [137] CompleteMessage: SessionId:[0], SequenceNumber:[1]
    2024-01-26T15:18:41.9229045-06:00 [ERR] [138] FAILURE: The sesion lock *should* have been lost, but was not
