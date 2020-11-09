﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Client.Transport.Mqtt;
using OpcPublisher.Configurations;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OpcPublisher
{
    /// <summary>
    /// Class to encapsulate the IoTHub device/module client interface.
    /// </summary>
    public class HubClientWrapper
    {


        /// <summary>
        /// Stores custom product information that will be appended to the user agent string that is sent to IoT Hub.
        /// </summary>
        public string ProductInfo
        {
            get
            {
                if (_iotHubClient == null)
                {
                    return _edgeHubClient.ProductInfo;
                }
                return _iotHubClient.ProductInfo;
            }
            set
            {
                if (_iotHubClient == null)
                {
                    _edgeHubClient.ProductInfo = value;
                    return;
                }
                _iotHubClient.ProductInfo = value;
            }
        }

        /// <summary>
        /// Close the client instance
        /// </summary>
        public void Close()
        {
            // send cancellation token and wait for last IoT Hub message to be sent.
            _hubCommunicationCts?.Cancel();
            try
            {
                _monitoredItemsProcessorTask?.Wait();
                _monitoredItemsProcessorTask = null;
                _monitoredItemsDataQueue = null;

                if (_edgeHubClient != null)
                {
                    _edgeHubClient.CloseAsync().Wait();
                }

                if (_iotHubClient != null)
                {
                    _iotHubClient.CloseAsync().Wait();
                }
            }
            catch (Exception e)
            {
                Program.Instance.Logger.Error(e, "Failure while shutting down hub messaging.");
            }
        }

        /// <summary>
        /// Handle connection status change notifications.
        /// </summary>
        private void ConnectionStatusChange(ConnectionStatus status, ConnectionStatusChangeReason reason)
        {
            if (reason == ConnectionStatusChangeReason.Connection_Ok || Program.Instance.ShutdownTokenSource.IsCancellationRequested)
            {
                Program.Instance.Logger.Information($"IoT Hub Connection status changed to '{status}', reason '{reason}'");
            }
            else
            {
                Program.Instance.Logger.Error($"IoT Hub Connection status changed to '{status}', reason '{reason}'");
            }
        }

        /// <summary>
        /// Initializes edge message broker communication.
        /// </summary>
        public void InitHubCommunication(UAClient uaClient, bool runningInIoTEdgeContext, string connectionString)
        {
            _hubMethodHandler = new HubMethodHandler(uaClient);

            ExponentialBackoff exponentialRetryPolicy = new ExponentialBackoff(int.MaxValue, TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(1024), TimeSpan.FromMilliseconds(3));

            // open connection
            Program.Instance.Logger.Debug($"Open hub communication");
            if (runningInIoTEdgeContext)
            {
                Program.Instance.Logger.Information($"Creating IoT Edge module client using '{SettingsConfiguration.EdgeHubProtocol}' for communication.");

                if (string.IsNullOrEmpty(connectionString))
                {
                    _edgeHubClient = ModuleClient.CreateFromEnvironmentAsync(SettingsConfiguration.EdgeHubProtocol).Result;
                }
                else
                {
                    _edgeHubClient = ModuleClient.CreateFromConnectionString(connectionString, SettingsConfiguration.EdgeHubProtocol);
                }

                _hubMethodHandler.RegisterMethodHandlers(_edgeHubClient);
            }
            else
            {
                if (string.IsNullOrEmpty(connectionString))
                {
                    string errorMessage = $"Please pass the device connection string in via command line option. Can not connect to IoTHub. Exiting...";
                    Program.Instance.Logger.Fatal(errorMessage);
                    throw new ArgumentException(errorMessage);
                }

                Program.Instance.Logger.Information($"Creating IoT Hub device client using '{SettingsConfiguration.IotHubProtocol}' for communication.");

                if (connectionString.Contains(";GatewayHostName="))
                {
                    // transparent gateway mode
                    List<ITransportSettings> transportSettingsList = new List<ITransportSettings>();
                    MqttTransportSettings transportSettings = new MqttTransportSettings(TransportType.Mqtt_Tcp_Only);
                    transportSettings.RemoteCertificateValidationCallback = (sender, certificate, chain, sslPolicyErrors) => true;
                    transportSettingsList.Add(transportSettings);
                    _iotHubClient = DeviceClient.CreateFromConnectionString(connectionString, transportSettingsList.ToArray());
                }
                else
                {
                    _iotHubClient = DeviceClient.CreateFromConnectionString(connectionString, TransportType.Mqtt);
                }

                _hubMethodHandler.RegisterMethodHandlers(_iotHubClient);
            }

            ProductInfo = "OpcPublisher";
            SetRetryPolicy(exponentialRetryPolicy);

            // register connection status change handler
            SetConnectionStatusChangesHandler(ConnectionStatusChange);
        }

        /// <summary>
        /// Initializes internal message processing.
        /// </summary>
        public void InitMessageProcessing()
        {
            try
            {
                // show config
                Program.Instance.Logger.Information($"Message processing and hub communication configured with a send interval of {SettingsConfiguration.DefaultSendIntervalSeconds} sec and a message buffer size of {SettingsConfiguration.HubMessageSize} bytes.");

                // create the queue for monitored items
                _monitoredItemsDataQueue = new BlockingCollection<MessageDataModel>(SettingsConfiguration.MonitoredItemsQueueCapacity);

                // start up task to send telemetry to IoTHub
                Program.Instance.Logger.Information("Creating task process and batch monitored item data updates...");
                _monitoredItemsProcessorTask = Task.Run(() => MonitoredItemsProcessorAsync(_hubCommunicationCts.Token));
            }
            catch (Exception e)
            {
                Program.Instance.Logger.Error(e, "Failure initializing message processing.");
                throw e;
            }
        }

        /// <summary>
        /// Dequeue monitored item notification messages, batch them for send (if needed) and send them to IoTHub.
        /// </summary>
        public async Task MonitoredItemsProcessorAsync(CancellationToken cancellationToken)
        {
            uint jsonSquareBracketLength = 2;
            Message tempMsg = new Message();
            
            // the system properties are MessageId (max 128 byte), Sequence number (ulong), ExpiryTime (DateTime) and more. ideally we get that from the client.
            int systemPropertyLength = 128 + sizeof(ulong) + tempMsg.ExpiryTimeUtc.ToString(CultureInfo.InvariantCulture).Length;
            int applicationPropertyLength = Encoding.UTF8.GetByteCount($"iothub-content-type={CONTENT_TYPE_OPCUAJSON}") + Encoding.UTF8.GetByteCount($"iothub-content-encoding={CONTENT_ENCODING_UTF8}");
            
            // if batching is requested the buffer will have the requested size, otherwise we reserve the max size
            uint hubMessageBufferSize = (SettingsConfiguration.HubMessageSize > 0 ? SettingsConfiguration.HubMessageSize : SettingsConfiguration.HubMessageSizeMax) - (uint)systemPropertyLength - jsonSquareBracketLength - (uint)applicationPropertyLength;
            byte[] hubMessageBuffer = new byte[hubMessageBufferSize];
            MemoryStream hubMessage = new MemoryStream(hubMessageBuffer);
            DateTime nextSendTime = DateTime.UtcNow + TimeSpan.FromSeconds(SettingsConfiguration.DefaultSendIntervalSeconds);
            bool singleMessageSend = SettingsConfiguration.DefaultSendIntervalSeconds == 0 && SettingsConfiguration.HubMessageSize == 0;

            using (hubMessage)
            {
                try
                {
                    string jsonMessage = string.Empty;
                    MessageDataModel messageData = new MessageDataModel();
                    bool needToBufferMessage = false;
                    int jsonMessageSize = 0;

                    hubMessage.Position = 0;
                    hubMessage.SetLength(0);
                    if (!singleMessageSend)
                    {
                        hubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                    }
                    while (true)
                    {
                        TimeSpan timeTillNextSend;
                        int millisToWait;
                        // sanity check the send interval, compute the timeout and get the next monitored item message
                        if (SettingsConfiguration.DefaultSendIntervalSeconds > 0)
                        {
                            timeTillNextSend = nextSendTime.Subtract(DateTime.UtcNow);
                            if (timeTillNextSend < TimeSpan.Zero)
                            {
                                Metrics.MissedSendIntervalCount++;
                                // do not wait if we missed the send interval
                                timeTillNextSend = TimeSpan.Zero;
                            }

                            long millisLong = (long)timeTillNextSend.TotalMilliseconds;
                            if (millisLong < 0 || millisLong > int.MaxValue)
                            {
                                millisToWait = 0;
                            }
                            else
                            {
                                millisToWait = (int)millisLong;
                            }
                        }
                        else
                        {
                            // if we are in shutdown do not wait, else wait infinite if send interval is not set
                            millisToWait = cancellationToken.IsCancellationRequested ? 0 : Timeout.Infinite;
                        }

                        bool gotItem = _monitoredItemsDataQueue.TryTake(out messageData, millisToWait, cancellationToken);

                        EventMessageDataModel eventMessageData = null;
                        MessageDataModel dataChangeMessageData = null;
                        if (messageData is EventMessageDataModel model)
                        {
                            eventMessageData = model;
                        }
                        else
                        {
                            dataChangeMessageData = messageData;
                        }

                        // the two commandline parameter --ms (message size) and --si (send interval) control when data is sent to IoTHub/EdgeHub
                        // pls see detailed comments on performance and memory consumption at https://github.com/Azure/iot-edge-opc-publisher

                        // check if we got an item or if we hit the timeout or got canceled
                        if (gotItem)
                        {
                            if (dataChangeMessageData != null)
                            {
                                jsonMessage = await SimpleTelemetryEncoder.CreateJsonForDataChangeAsync(dataChangeMessageData).ConfigureAwait(false);
                            }
                            if (eventMessageData != null)
                            {
                                jsonMessage = await SimpleTelemetryEncoder.CreateJsonForEventAsync(eventMessageData).ConfigureAwait(false);
                            }

                            Metrics.NumberOfEvents++;
                            jsonMessageSize = Encoding.UTF8.GetByteCount(jsonMessage);

                            // sanity check that the user has set a large enough messages size
                            if ((SettingsConfiguration.HubMessageSize > 0 && jsonMessageSize > SettingsConfiguration.HubMessageSize) || (SettingsConfiguration.HubMessageSize == 0 && jsonMessageSize > hubMessageBufferSize))
                            {
                                Program.Instance.Logger.Error($"There is a telemetry message (size: {jsonMessageSize}), which will not fit into an hub message (max size: {hubMessageBufferSize}].");
                                Program.Instance.Logger.Error($"Please check your hub message size settings. The telemetry message will be discarded.");
                                Metrics.TooLargeCount++;
                                continue;
                            }

                            // if batching is requested or we need to send at intervals, batch it otherwise send it right away
                            needToBufferMessage = false;
                            if (SettingsConfiguration.HubMessageSize > 0 || (SettingsConfiguration.HubMessageSize == 0 && SettingsConfiguration.DefaultSendIntervalSeconds > 0))
                            {
                                // if there is still space to batch, do it. otherwise send the buffer and flag the message for later buffering
                                if (hubMessage.Position + jsonMessageSize + 1 <= hubMessage.Capacity)
                                {
                                    // add the message and a comma to the buffer
                                    hubMessage.Write(Encoding.UTF8.GetBytes(jsonMessage), 0, jsonMessageSize);
                                    hubMessage.Write(Encoding.UTF8.GetBytes(","), 0, 1);
                                    Program.Instance.Logger.Debug($"Added new message with size {jsonMessageSize} to hub message (size is now {hubMessage.Position - 1}).");
                                    continue;
                                }
                                else
                                {
                                    needToBufferMessage = true;
                                }
                            }
                        }
                        else
                        {
                            // if we got no message, we either reached the interval or we are in shutdown and have processed all messages
                            if (cancellationToken.IsCancellationRequested)
                            {
                                Program.Instance.Logger.Information($"Cancellation requested.");
                                _monitoredItemsDataQueue.CompleteAdding();
                                break;
                            }
                        }

                        // the batching is completed or we reached the send interval or got a cancelation request
                        try
                        {
                            Message encodedhubMessage = null;

                            // if we reached the send interval, but have nothing to send (only the opening square bracket is there), we continue
                            if (!gotItem && hubMessage.Position == 1)
                            {
                                Program.Instance.Logger.Verbose("Adding {seconds} seconds to current nextSendTime {nextSendTime}...", SettingsConfiguration.DefaultSendIntervalSeconds, nextSendTime);
                                nextSendTime += TimeSpan.FromSeconds(SettingsConfiguration.DefaultSendIntervalSeconds);
                                hubMessage.Position = 0;
                                hubMessage.SetLength(0);
                                if (!singleMessageSend)
                                {
                                    hubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                                }
                                continue;
                            }

                            // if there is no batching and no send interval configured, we send the JSON message we just got, otherwise we send the buffer
                            if (singleMessageSend)
                            {
                                // create the message without brackets
                                encodedhubMessage = new Message(Encoding.UTF8.GetBytes(jsonMessage));
                            }
                            else
                            {
                                // remove the trailing comma and add a closing square bracket
                                hubMessage.SetLength(hubMessage.Length - 1);
                                hubMessage.Write(Encoding.UTF8.GetBytes("]"), 0, 1);
                                encodedhubMessage = new Message(hubMessage.ToArray());
                            }
                            
                            encodedhubMessage.ContentType = CONTENT_TYPE_OPCUAJSON;
                            encodedhubMessage.ContentEncoding = CONTENT_ENCODING_UTF8;

                            if (nextSendTime < DateTime.UtcNow)
                            {
                                Program.Instance.Logger.Verbose("Adding {seconds} seconds to current nextSendTime {nextSendTime}...", SettingsConfiguration.DefaultSendIntervalSeconds, nextSendTime);
                                nextSendTime += TimeSpan.FromSeconds(SettingsConfiguration.DefaultSendIntervalSeconds);
                            }

                            try
                            {
                                Metrics.SentBytes += encodedhubMessage.GetBytes().Length;
                                SendEvent(encodedhubMessage);
                                Metrics.SentMessages++;
                                Metrics.SentLastTime = DateTime.UtcNow;
                                Program.Instance.Logger.Debug($"Sending {encodedhubMessage.BodyStream.Length} bytes to hub.");
                            }
                            catch
                            {
                                Metrics.FailedMessages++;
                            }

                            // reset the messaage
                            hubMessage.Position = 0;
                            hubMessage.SetLength(0);
                            if (!singleMessageSend)
                            {
                                hubMessage.Write(Encoding.UTF8.GetBytes("["), 0, 1);
                            }

                            // if we had not yet buffered the last message because there was not enough space, buffer it now
                            if (needToBufferMessage)
                            {
                                // add the message and a comma to the buffer
                                hubMessage.Write(Encoding.UTF8.GetBytes(jsonMessage), 0, jsonMessageSize);
                                hubMessage.Write(Encoding.UTF8.GetBytes(","), 0, 1);
                            }
                        }
                        catch (Exception e)
                        {
                            Program.Instance.Logger.Error(e, "Exception while sending message to hub. Dropping message...");
                        }
                    }
                }
                catch (Exception e)
                {
                    if (!(e is OperationCanceledException))
                    {
                        Program.Instance.Logger.Error(e, "Error while processing monitored item messages.");
                    }
                }
            }
        }

        /// <summary>
        /// Sets the retry policy used in the operation retries.
        /// </summary>
        public void SetRetryPolicy(IRetryPolicy retryPolicy)
        {
            if (_iotHubClient == null)
            {
                _edgeHubClient.SetRetryPolicy(retryPolicy);
                return;
            }
            _iotHubClient.SetRetryPolicy(retryPolicy);
        }

        /// <summary>
        /// Registers a new delegate for the connection status changed callback. If a delegate is already associated,
        /// it will be replaced with the new delegate.
        /// </summary>
        public void SetConnectionStatusChangesHandler(ConnectionStatusChangesHandler statusChangesHandler)
        {
            if (_iotHubClient == null)
            {
                _edgeHubClient.SetConnectionStatusChangesHandler(statusChangesHandler);
                return;
            }
            _iotHubClient.SetConnectionStatusChangesHandler(statusChangesHandler);
        }

        /// <summary>
        /// Sends an event to device hub
        /// </summary>
        public void SendEvent(Message message)
        {
            Stopwatch watch = new Stopwatch();
            watch.Start();
            if (_edgeHubClient != null)
            {
                _edgeHubClient.SendEventAsync(message).Wait();
            }
            else
            {
                if (_iotHubClient != null)
                {
                    _iotHubClient.SendEventAsync(message).Wait();
                }
            }
            watch.Stop();

            _lastMessageLatencies.Enqueue(watch.ElapsedMilliseconds);

            // calc the average for the last 100 messages
            if (_lastMessageLatencies.Count > 100)
            {
                _lastMessageLatencies.Dequeue();
            }

            long sum = 0;
            foreach (long latency in _lastMessageLatencies)
            {
                sum += latency;
            }

            Metrics.AverageMessageLatency = sum / _lastMessageLatencies.Count;
        }

        /// <summary>
        /// Enqueue a message for sending to IoTHub.
        /// </summary>
        public static void Enqueue(MessageDataModel json)
        {
            // Try to add the message.
            if (_monitoredItemsDataQueue != null)
            {
                Metrics.EnqueueCount++;
                if (_monitoredItemsDataQueue.TryAdd(json) == false)
                {
                    Metrics.EnqueueFailureCount++;
                    if (Metrics.EnqueueFailureCount % 10000 == 0)
                    {
                        Program.Instance.Logger.Information($"The internal monitored item message queue is above its capacity of {_monitoredItemsDataQueue.BoundedCapacity}. We have lost {Metrics.EnqueueFailureCount} monitored item notifications so far.");
                    }
                }
                else
                {
                    Metrics.MonitoredItemsQueueCount++;
                }
            }
        }

        private const string CONTENT_TYPE_OPCUAJSON = "application/opcua+uajson";
        private const string CONTENT_ENCODING_UTF8 = "UTF-8";
        
        private static BlockingCollection<MessageDataModel> _monitoredItemsDataQueue;

        private Task _monitoredItemsProcessorTask;
        private CancellationTokenSource _hubCommunicationCts = new CancellationTokenSource();

        private DeviceClient _iotHubClient;
        private ModuleClient _edgeHubClient;

        private HubMethodHandler _hubMethodHandler;

        private static Queue<long> _lastMessageLatencies = new Queue<long>();
    }
}
