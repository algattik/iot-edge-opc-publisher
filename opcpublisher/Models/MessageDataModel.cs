﻿// ------------------------------------------------------------
//  Copyright (c) Microsoft Corporation.  All rights reserved.
//  Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------


namespace OpcPublisher
{
    /// <summary>
    /// Class used to pass data from the MonitoredItem notification to the hub message processing.
    /// </summary>
    public class MessageDataModel
    {
        /// <summary>
        /// The endpoint URL the monitored item belongs to.
        /// </summary>
        public string EndpointUrl { get; set; }

        /// <summary>
        /// The OPC UA NodeId of the monitored item.
        /// </summary>
        public string NodeId { get; set; }

        /// <summary>
        /// The OPC UA Node Id with the namespace expanded.
        /// </summary>
        public string ExpandedNodeId { get; set; }

        /// <summary>
        /// The Application URI of the OPC UA server the node belongs to.
        /// </summary>
        public string ApplicationUri { get; set; }

        /// <summary>
        /// The display name of the node.
        /// </summary>
        public string DisplayName { get; set; }

        /// <summary>
        /// The value of the node.
        /// </summary>
        public string Value { get; set; }

        /// <summary>
        /// The OPC UA source timestamp the value was seen.
        /// </summary>
        public string SourceTimestamp { get; set; }

        /// <summary>
        /// The OPC UA status code of the value.
        /// </summary>
        public uint? StatusCode { get; set; }

        /// <summary>
        /// The OPC UA status of the value.
        /// </summary>
        public string Status { get; set; }

        /// <summary>
        /// Flag if the encoding of the value should preserve quotes.
        /// </summary>
        public bool PreserveValueQuotes { get; set; }

        /// <summary>
        /// Ctor of the object.
        /// </summary>
        public MessageDataModel()
        {
            EndpointUrl = null;
            NodeId = null;
            ExpandedNodeId = null;
            ApplicationUri = null;
            DisplayName = null;
            Value = null;
            StatusCode = null;
            SourceTimestamp = null;
            Status = null;
            PreserveValueQuotes = false;
        }
    }
}
