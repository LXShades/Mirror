using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mirror
{
    /// <summary>Base NetworkConnection class for server-to-client and client-to-server connection.</summary>
    public abstract class NetworkConnection
    {
        public struct FlowControlledMessage
        {
            public byte[] data;
            public int channelId;

            public FlowControlledMessage(byte[] data, int channelId)
            {
                this.data = data;
                this.channelId = channelId;
            }
        }

        public const int LocalConnectionId = 0;

        // NetworkIdentities that this connection can see
        // TODO move to server's NetworkConnectionToClient?
        internal readonly HashSet<NetworkIdentity> observing = new HashSet<NetworkIdentity>();

        // TODO this is NetworkServer.handlers on server and NetworkClient.handlers on client.
        //      maybe use them directly. avoid extra state.
        Dictionary<ushort, NetworkMessageDelegate> messageHandlers;

        /// <summary>Unique identifier for this connection that is assigned by the transport layer.</summary>
        // assigned by transport, this id is unique for every connection on server.
        // clients don't know their own id and they don't know other client's ids.
        public readonly int connectionId;

        /// <summary>Flag that indicates the client has been authenticated.</summary>
        public bool isAuthenticated;

        /// <summary>General purpose object to hold authentication data, character selection, tokens, etc.</summary>
        public object authenticationData;

        /// <summary>A server connection is ready after joining the game world.</summary>
        // TODO move this to ConnectionToClient so the flag only lives on server
        // connections? clients could use NetworkClient.ready to avoid redundant
        // state.
        public bool isReady;

        /// <summary>IP address of the connection. Can be useful for game master IP bans etc.</summary>
        public abstract string address { get; }

        /// <summary>Last time a message was received for this connection. Includes system and user messages.</summary>
        public float lastMessageTime;

        /// <summary>This connection's main object (usually the player object).</summary>
        public NetworkIdentity identity { get; internal set; }

        /// <summary>All NetworkIdentities owned by this connection. Can be main player, pets, etc.</summary>
        // IMPORTANT: this needs to be <NetworkIdentity>, not <uint netId>.
        //            fixes a bug where DestroyOwnedObjects wouldn't find the
        //            netId anymore: https://github.com/vis2k/Mirror/issues/1380
        //            Works fine with NetworkIdentity pointers though.
        public readonly HashSet<NetworkIdentity> clientOwnedObjects = new HashSet<NetworkIdentity>();

        /// <summary>Whether this connection uses flow control.
        /// Note that there may still be network messages buffered even if this is false. Any of these should be flushed via TryReleaseFlowControlledMessages.</summary>
        public bool isFlowControlled { get; set; }

        /// <summary>Flow controller</summary>
        public readonly FlowController<FlowControlledMessage> flowController = new FlowController<FlowControlledMessage>();

        internal NetworkConnection()
        {
            // set lastTime to current time when creating connection to make
            // sure it isn't instantly kicked for inactivity
            lastMessageTime = Time.time;
        }

        internal NetworkConnection(int networkConnectionId) : this()
        {
            connectionId = networkConnectionId;
            // TODO why isn't lastMessageTime set in here like in the other ctor?
        }

        /// <summary>Disconnects this connection.</summary>
        public abstract void Disconnect();

        internal void SetHandlers(Dictionary<ushort, NetworkMessageDelegate> handlers)
        {
            messageHandlers = handlers;
        }

        /// <summary>Send a NetworkMessage to this connection over the given channel.</summary>
        public void Send<T>(T msg, int channelId = Channels.Reliable)
            where T : struct, NetworkMessage
        {
            using (PooledNetworkWriter writer = NetworkWriterPool.GetWriter())
            {
                // pack message and send allocation free
                MessagePacking.Pack(msg, writer);
                NetworkDiagnostics.OnSend(msg, channelId, writer.Position, 1);
                Send(writer.ToArraySegment(), channelId);
            }
        }

        // validate packet size before sending. show errors if too big/small.
        // => it's best to check this here, we can't assume that all transports
        //    would check max size and show errors internally. best to do it
        //    in one place in hlapi.
        // => it's important to log errors, so the user knows what went wrong.
        protected static bool ValidatePacketSize(ArraySegment<byte> segment, int channelId)
        {
            if (segment.Count > Transport.activeTransport.GetMaxPacketSize(channelId))
            {
                Debug.LogError($"NetworkConnection.ValidatePacketSize: cannot send packet larger than {Transport.activeTransport.GetMaxPacketSize(channelId)} bytes, was {segment.Count} bytes");
                return false;
            }

            if (segment.Count == 0)
            {
                // zero length packets getting into the packet queues are bad.
                Debug.LogError("NetworkConnection.ValidatePacketSize: cannot send zero bytes");
                return false;
            }

            // good size
            return true;
        }

        // internal because no one except Mirror should send bytes directly to
        // the client. they would be detected as a message. send messages instead.
        internal abstract void Send(ArraySegment<byte> segment, int channelId = Channels.Reliable);

        public override string ToString() => $"connection({connectionId})";

        // TODO move to server's NetworkConnectionToClient?
        internal void AddToObserving(NetworkIdentity netIdentity)
        {
            observing.Add(netIdentity);

            // spawn identity for this conn
            NetworkServer.ShowForConnection(netIdentity, this);
        }

        // TODO move to server's NetworkConnectionToClient?
        internal void RemoveFromObserving(NetworkIdentity netIdentity, bool isDestroyed)
        {
            observing.Remove(netIdentity);

            if (!isDestroyed)
            {
                // hide identity for this conn
                NetworkServer.HideForConnection(netIdentity, this);
            }
        }

        // TODO move to server's NetworkConnectionToClient?
        internal void RemoveObservers()
        {
            foreach (NetworkIdentity netIdentity in observing)
            {
                netIdentity.RemoveObserverInternal(this);
            }
            observing.Clear();
        }

        // converts a packed message time (cycling ushort) to a flow controller-compatible time (linear float)
        private float PackedTicksToFlowControllerTime(ushort packedTicks)
        {
            if (flowController.lastPoppedMessageSentTime >= 0f)
            {
                // imagine packedTicks being the decimal and SecondsPerMaxPackedTicks is the integer
                // when the decimal wraps around to 0, we can assume the integer increased (as time always goes forwards)
                float convertedTime = ((float)packedTicks / 1000f) + (int)(flowController.lastPoppedMessageSentTime / MessagePacking.SecondsPerMaxPackedTicks) * MessagePacking.SecondsPerMaxPackedTicks;

                if (convertedTime < flowController.lastPoppedMessageSentTime)
                {
                    // increase the "integer"
                    convertedTime += MessagePacking.SecondsPerMaxPackedTicks;
                }

                return convertedTime;
            }

            return (float)packedTicks / MessagePacking.PackedTicksPerSecond;
        }

        // helper function
        protected bool UnpackAndInvoke(NetworkReader reader, int channelId, bool shouldForceInvoke = false)
        {
            // even if flow control is disabled, we should still check for pending messages - it might have only just been disabled.
            // to maintain packet order, we must continue to collect into the flow controller until they are all flushed in the next EarlyUpdate
            if ((isFlowControlled || flowController.numBufferedPendingMessages > 0) && !shouldForceInvoke)
            {
                // collect the message pack into the buffer
                // extract the packedTime first
                int prevPosition = reader.Position;

                if (!MessagePacking.Unpack(reader, out ushort _, out ushort msgTime))
                {
                    Debug.LogError("Closed connection: " + this + ". Invalid message header.");
                    Disconnect();
                    return false;
                }

                reader.Position = prevPosition;
                flowController.PushMessage(new FlowControlledMessage(reader.ReadBytes(reader.Length), channelId), PackedTicksToFlowControllerTime(msgTime));

                // we _got_ it, but we'll invoke it later
                lastMessageTime = Time.time;
                return true;
            }
            else
            {
                if (MessagePacking.Unpack(reader, out ushort msgType, out ushort _))
                {
                    // try to invoke the handler for that message
                    if (messageHandlers.TryGetValue(msgType, out NetworkMessageDelegate msgDelegate))
                    {
                        msgDelegate.Invoke(this, reader, channelId);
                        lastMessageTime = Time.time;
                        return true;
                    }
                    else
                    {
                        // Debug.Log("Unknown message ID " + msgType + " " + this + ". May be due to no existing RegisterHandler for this message.");
                        return false;
                    }
                }
                else
                {
                    Debug.LogError("Closed connection: " + this + ". Invalid message header.");
                    Disconnect();
                    return false;
                }
            }
        }

        // called when receiving data from the transport
        internal void TransportReceive(ArraySegment<byte> buffer, int channelId)
        {
            if (buffer.Count < MessagePacking.HeaderSize)
            {
                Debug.LogError($"ConnectionRecv {this} Message was too short (messages should start with message id)");
                Disconnect();
                return;
            }

            // unpack message
            using (PooledNetworkReader reader = NetworkReaderPool.GetReader(buffer))
            {
                // the other end might batch multiple messages into one packet.
                // we need to try to unpack multiple times.
                while (reader.Position < reader.Length)
                {
                    if (!UnpackAndInvoke(reader, channelId))
                        break;
                }
            }
        }

        // releases flow controlled messages if applicable
        internal void TryReleaseFlowControlledMessages()
        {
            // pop any remaining messages even if isFlowControlled is now false - we must empty the buffer before we can return to realtime
            while (flowController.TryPopMessage(out FlowControlledMessage message, false))
            {
                using (PooledNetworkReader reader = NetworkReaderPool.GetReader(message.data))
                {
                    while (reader.Position < reader.Length)
                    {
                        if (!UnpackAndInvoke(reader, message.channelId, true))
                            break;
                    }
                }
            }
        }

        /// <summary>Check if we received a message within the last 'timeout' seconds.</summary>
        internal virtual bool IsAlive(float timeout) => Time.time - lastMessageTime < timeout;

        internal void AddOwnedObject(NetworkIdentity obj)
        {
            clientOwnedObjects.Add(obj);
        }

        internal void RemoveOwnedObject(NetworkIdentity obj)
        {
            clientOwnedObjects.Remove(obj);
        }

        internal void DestroyOwnedObjects()
        {
            // create a copy because the list might be modified when destroying
            HashSet<NetworkIdentity> tmp = new HashSet<NetworkIdentity>(clientOwnedObjects);
            foreach (NetworkIdentity netIdentity in tmp)
            {
                if (netIdentity != null)
                {
                    NetworkServer.Destroy(netIdentity.gameObject);
                }
            }

            // clear the hashset because we destroyed them all
            clientOwnedObjects.Clear();
        }
    }
}
