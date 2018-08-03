﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;

using Lidgren.Network;

namespace Microsoft.Xna.Framework.Net.Backend.Lidgren
{
    internal class IntroducerToken
    {
        public IntroducerToken(LidgrenGuidEndPoint hostEndPoint, IPEndPoint hostExternalIp, IPEndPoint clientExternalIp)
        {
            HostEndPoint = hostEndPoint ?? throw new ArgumentNullException(nameof(hostEndPoint));
            HostExternalIp = hostExternalIp ?? throw new ArgumentNullException(nameof(hostExternalIp));
            ClientExternalIp = clientExternalIp ?? throw new ArgumentNullException(nameof(clientExternalIp));
        }

        /// <summary>
        /// The host end point identification
        /// </summary>
        public LidgrenGuidEndPoint HostEndPoint { get; private set; }

        /// <summary>
        /// The external ip of the host as observed by the introducer
        /// </summary>
        public IPEndPoint HostExternalIp { get; private set; }

        /// <summary>
        /// The external ip of the client as observed by the introducer
        /// </summary>
        public IPEndPoint ClientExternalIp { get; private set; }

        public string Serialize()
        {
            var hostEndPoint = HostEndPoint.ToString();
            var hostAddress = HostExternalIp.Address.ToString();
            var hostPort = HostExternalIp.Port.ToString();
            var clientAddress = ClientExternalIp.Address.ToString();
            var clientPort = ClientExternalIp.Port.ToString();
            return string.Join(";", new string[] {
                hostEndPoint, hostAddress, hostPort, clientAddress, clientPort
            });
        }

        public static bool Deserialize(string str, out IntroducerToken token)
        {
            if (str == null)
            {
                throw new ArgumentNullException(nameof(str));
            }
            var parts = str.Split(';');
            if (parts.Length != 5)
            {
                token = null;
                return false;
            }
            try
            {
                var hostEndPoint = LidgrenGuidEndPoint.Parse(parts[0]);
                var hostAddress = IPAddress.Parse(parts[1]);
                var hostPort = int.Parse(parts[2]);
                var clientAddress = IPAddress.Parse(parts[3]);
                var clientPort = int.Parse(parts[4]);

                token = new IntroducerToken(hostEndPoint,
                                                    new IPEndPoint(hostAddress, hostPort),
                                                    new IPEndPoint(clientAddress, clientPort));
            }
            catch
            {
                token = null;
            }
            return token != null;
        }
    }

    internal class LidgrenGuidEndPoint : PeerEndPoint
    {
        public static LidgrenGuidEndPoint Parse(string input)
        {
            Guid guid;
            try { guid = Guid.Parse(input); }
            catch { guid = Guid.Empty; }
            return new LidgrenGuidEndPoint(guid);
        }

        private Guid guid;

        public LidgrenGuidEndPoint()
        {
            guid = Guid.NewGuid();
        }

        private LidgrenGuidEndPoint(Guid guid)
        {
            this.guid = guid;
        }

        public override bool Equals(object obj)
        {
            var otherLidgren = obj as LidgrenGuidEndPoint;
            if (otherLidgren == null)
            {
                return false;
            }
            return guid.Equals(otherLidgren.guid);
        }

        public override int GetHashCode()
        {
            return guid.GetHashCode();
        }

        public override bool Equals(PeerEndPoint other)
        {
            var otherLidgren = other as LidgrenGuidEndPoint;
            if (otherLidgren == null)
            {
                return false;
            }
            return guid.Equals(otherLidgren.guid);
        }

        public override string ToString()
        {
            return guid.ToString();
        }
    }

    internal abstract class LidgrenPeer : Peer
    {
        public abstract long SessionId { get; }
    }

    internal class RemotePeer : LidgrenPeer
    {
        private NetConnection connection;
        private LidgrenGuidEndPoint endPoint;
        private IPEndPoint internalIp;
        private IPEndPoint externalIp;

        public RemotePeer(NetConnection connection, LidgrenGuidEndPoint endPoint, IPEndPoint internalIp, IPEndPoint externalIp)
        {
            this.connection = connection;
            this.endPoint = endPoint;
            this.internalIp = internalIp;
            this.externalIp = externalIp;
        }

        public NetConnection Connection { get { return connection; } }
        public IPEndPoint InternalIp { get { return internalIp; } } // Might differ from Connection!
        public IPEndPoint ExternalIp { get { return externalIp; } } // Might differ from Connection!

        public override PeerEndPoint EndPoint { get { return endPoint; } }
        public override long SessionId { get { return connection.RemoteUniqueIdentifier; } }
        public override TimeSpan RoundtripTime { get { return TimeSpan.FromSeconds(connection.AverageRoundtripTime); } }
        public override object Tag { get; set; }

        public override void Disconnect(string byeMessage)
        {
            connection.Disconnect(byeMessage);
        }
    }

    internal class LocalPeer : LidgrenPeer
    {
        private LidgrenBackend backend;
        private NetPeer peer;
        private LidgrenGuidEndPoint endPoint;

        public LocalPeer(LidgrenBackend backend, NetPeer peer)
        {
            this.backend = backend;
            this.peer = peer;
            this.endPoint = new LidgrenGuidEndPoint();
        }

        public bool HasShutdown { get; private set; }
        public ISessionBackendListener Listener { get; set; }

        public TimeSpan SimulatedLatency
        {
#if DEBUG
            get { return TimeSpan.FromSeconds(peer.Configuration.SimulatedRandomLatency); }
            set { peer.Configuration.SimulatedRandomLatency = (float)value.TotalSeconds; }
#else
            get { return TimeSpan.Zero; }
            set { }
#endif
        }

        public float SimulatedPacketLoss
        {
#if DEBUG
            get { return peer.Configuration.SimulatedLoss; }
            set { peer.Configuration.SimulatedLoss = value; }
#else
            get { return 0.0f; }
            set { }
#endif
        }

        public int TotalReceivedBytes
        {
            get { return peer.Statistics.ReceivedBytes;  }
        }

        public int TotalSentBytes
        {
            get { return peer.Statistics.SentBytes; }
        }

        public IPEndPoint InternalIp
        {
            get
            {
                IPAddress mask;
                IPAddress address = NetUtility.GetMyAddress(out mask);
                return new IPEndPoint(address, peer.Port);
            }
        }

        public override PeerEndPoint EndPoint { get { return endPoint; } }
        public override long SessionId { get { return peer.UniqueIdentifier; } }
        public override TimeSpan RoundtripTime { get { return TimeSpan.Zero; } }
        public override object Tag { get; set; }

        public void Introduce(RemotePeer remoteClient, RemotePeer remoteHost)
        {
            Debug.WriteLine("Introducing client " + remoteClient.ExternalIp + " to host " + remoteHost.ExternalIp + "...");

            // The client will receive the NatIntroductionSuccess message
            string token = new IntroducerToken(remoteHost.EndPoint as LidgrenGuidEndPoint,
                                                        remoteHost.ExternalIp,
                                                        remoteClient.ExternalIp).Serialize();

            peer.Introduce(remoteHost.InternalIp, remoteHost.ExternalIp, remoteClient.InternalIp, remoteClient.ExternalIp, token);
        }

        public void Connect(IPEndPoint destinationIp, IPEndPoint destinationExternalIp, IPEndPoint observedExternalIp)
        {
            if (peer.GetConnection(destinationIp) == null)
            {
                var hailMsg = peer.CreateMessage();
                hailMsg.Write(endPoint.ToString());
                hailMsg.Write(InternalIp);
                hailMsg.Write(observedExternalIp);
                hailMsg.Write(destinationExternalIp);
                peer.Connect(destinationIp, hailMsg);
            }
        }

        public override void Disconnect(string byeMessage)
        {
            HasShutdown = true;

            UnregisterWithMasterServer();

            peer.Shutdown(byeMessage);
        }

        private NetDeliveryMethod ToDeliveryMethod(SendDataOptions options)
        {
            switch (options)
            {
                case SendDataOptions.InOrder:
                    return NetDeliveryMethod.UnreliableSequenced;
                case SendDataOptions.Reliable:
                    return NetDeliveryMethod.ReliableUnordered;
                case SendDataOptions.ReliableInOrder:
                    return NetDeliveryMethod.ReliableOrdered;
                case SendDataOptions.Chat:
                    return NetDeliveryMethod.ReliableUnordered;
                case SendDataOptions.Chat & SendDataOptions.InOrder:
                    return NetDeliveryMethod.ReliableOrdered;
                default:
                    throw new InvalidOperationException("Could not convert SendDataOptions!");
            }
        }

        public void SendMessage(LidgrenOutgoingMessage msg)
        {
            var outgoingMsg = peer.CreateMessage(msg.Buffer.LengthBytes);
            outgoingMsg.Write(msg.Buffer);

            if (msg.Recipient == null)
            {
                var connections = backend.RemoteConnections;
                if (connections.Count > 0)
                {
                    peer.SendMessage(outgoingMsg, connections, ToDeliveryMethod(msg.Options), msg.Channel);
                }
            }
            else
            {
                peer.SendMessage(outgoingMsg, (msg.Recipient as RemotePeer).Connection, ToDeliveryMethod(msg.Options), msg.Channel);
            }
        }

        public void ReceiveMessages(GenericPool<LidgrenOutgoingMessage> outgoingPool, GenericPool<LidgrenIncomingMessage> incomingPool)
        {
            NetIncomingMessage msg;
            while ((msg = peer.ReadMessage()) != null)
            {
                if (msg.MessageType == NetIncomingMessageType.DiscoveryRequest)
                {
                    if (Listener.IsDiscoverableLocally)
                    {
                        Debug.WriteLine("Discovery request received.");

                        var responseMsg = outgoingPool.Get();
                        responseMsg.Write(endPoint.ToString());
                        Listener.SessionPublicInfo.Pack(responseMsg);

                        var response = peer.CreateMessage();
                        response.Write(responseMsg.Buffer);
                        peer.SendDiscoveryResponse(response, msg.SenderEndPoint);
                        outgoingPool.Recycle(responseMsg);
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.NatIntroductionSuccess)
                {
                    // This peer is a client from a NAT introduction standpoint
                    var hostPunchIp = msg.SenderEndPoint;

                    Debug.WriteLine($"NAT introduction successful received from {hostPunchIp}");

                    if (IntroducerToken.Deserialize(msg.ReadString(), out IntroducerToken token))
                    {
                        if (Listener.AllowConnectionToHostAsClient(token.HostEndPoint))
                        {
                            Connect(hostPunchIp, token.HostExternalIp, token.ClientExternalIp);
                        }
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.ConnectionApproval)
                {
                    // This peer is a host from a NAT introduction standpoint
                    var clientEndPoint = LidgrenGuidEndPoint.Parse(msg.ReadString());
                    var clientInternalIp = msg.ReadIPEndPoint();
                    var clientExternalIp = msg.ReadIPEndPoint();
                    var hostExternalIp = msg.ReadIPEndPoint(); // From IntroducerToken above

                    if (Listener.AllowConnectionFromClient(clientEndPoint))
                    {
                        var hailMsg = peer.CreateMessage();
                        hailMsg.Write(endPoint.ToString());
                        hailMsg.Write(InternalIp);
                        hailMsg.Write(hostExternalIp); // External ip unknown to this peer, must come from outisde
                        msg.SenderConnection.Approve(hailMsg);
                    }
                    else
                    {
                        msg.SenderConnection.Deny("Connection denied");
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.StatusChanged)
                {
                    if (msg.SenderConnection == null)
                    {
                        throw new NetworkException("Sender connection is null");
                    }

                    var status = (NetConnectionStatus)msg.ReadByte();
                    Debug.WriteLine($"Status now: {status} (Reason: {msg.ReadString()})");

                    if (status == NetConnectionStatus.Connected)
                    {
                        var hailMsg = msg.SenderConnection.RemoteHailMessage;
                        var endPoint = LidgrenGuidEndPoint.Parse(hailMsg.ReadString());
                        var internalIp = hailMsg.ReadIPEndPoint();
                        var externalIp = hailMsg.ReadIPEndPoint();

                        var remotePeer = new RemotePeer(msg.SenderConnection, endPoint, internalIp, externalIp);
                        msg.SenderConnection.Tag = remotePeer;
                        backend.AddRemotePeer(remotePeer);
                        Listener.PeerConnected(remotePeer);
                    }
                    else if (status == NetConnectionStatus.Disconnected)
                    {
                        var disconnectedPeer = msg.SenderConnection.Tag as RemotePeer;
                        if (disconnectedPeer != null) // If null, host responded to connect then peer disconnected
                        {
                            backend.RemoveRemotePeer(disconnectedPeer);
                            Listener.PeerDisconnected(disconnectedPeer);
                        }
                    }
                }
                else if (msg.MessageType == NetIncomingMessageType.Data)
                {
                    if (msg.SenderConnection == null)
                    {
                        throw new NetworkException("Sender connection is null");
                    }

                    var incomingMsg = incomingPool.Get();
                    incomingMsg.Set(backend, msg);
                    Listener.ReceiveMessage(incomingMsg, msg.SenderConnection.Tag as RemotePeer);
                    incomingPool.Recycle(incomingMsg);
                }
                else
                {
                    switch (msg.MessageType)
                    {
                        case NetIncomingMessageType.VerboseDebugMessage:
                        case NetIncomingMessageType.DebugMessage:
                        case NetIncomingMessageType.WarningMessage:
                        case NetIncomingMessageType.ErrorMessage:
                            Debug.WriteLine($"Lidgren: {msg.ReadString()}");
                            break;
                        default:
                            Debug.WriteLine($"Unhandled type: {msg.MessageType}");
                            break;
                    }
                }

                peer.Recycle(msg);

                if (HasShutdown)
                {
                    return;
                }
            }
        }

        public void RegisterWithMasterServer(GenericPool<LidgrenOutgoingMessage> outgoingPool)
        {
            if (!Listener.IsDiscoverableOnline)
            {
                return;
            }

            var masterServerEndPoint = NetUtility.Resolve(NetworkSessionSettings.MasterServerAddress, NetworkSessionSettings.MasterServerPort);

            var msg = outgoingPool.Get();
            msg.Write(peer.Configuration.AppIdentifier);
            msg.Write((byte)MasterServerMessageType.RegisterHost);
            msg.Write(endPoint.ToString());
            msg.Write(InternalIp);
            Listener.SessionPublicInfo.Pack(msg);

            var request = peer.CreateMessage();
            request.Write(msg.Buffer);
            peer.SendUnconnectedMessage(request, masterServerEndPoint);

            outgoingPool.Recycle(msg);

            Debug.WriteLine("Registering with master server (EndPoint: " + endPoint + ", InternalIp: " + InternalIp + ")");
        }

        public void UnregisterWithMasterServer()
        {
            if (!Listener.IsDiscoverableOnline)
            {
                return;
            }

            var masterServerEndPoint = NetUtility.Resolve(NetworkSessionSettings.MasterServerAddress, NetworkSessionSettings.MasterServerPort);

            var msg = peer.CreateMessage();
            msg.Write(peer.Configuration.AppIdentifier);
            msg.Write((byte)MasterServerMessageType.UnregisterHost);
            msg.Write(endPoint.ToString());
            peer.SendUnconnectedMessage(msg, masterServerEndPoint);

            Debug.WriteLine("Unregistering with master server (EndPoint: " + endPoint + ")");
        }
    }

    internal class LidgrenBackend : SessionBackend
    {
        private LocalPeer localPeer;
        private List<RemotePeer> remotePeers = new List<RemotePeer>();
        private List<NetConnection> remoteConnections = new List<NetConnection>();

        private GenericPool<LidgrenOutgoingMessage> outgoingMessagePool = new GenericPool<LidgrenOutgoingMessage>();
        private GenericPool<LidgrenIncomingMessage> incomingMessagePool = new GenericPool<LidgrenIncomingMessage>();

        private DateTime lastMasterServerRegistration = DateTime.MinValue;
        private DateTime lastStatisticsUpdate = DateTime.Now;
        private int lastReceivedBytes = 0;
        private int lastSentBytes = 0;

        public LidgrenBackend(NetPeer peer)
        {
            localPeer = new LocalPeer(this, peer);

            RemotePeers = new ReadOnlyCollection<RemotePeer>(remotePeers);
            RemoteConnections = new ReadOnlyCollection<NetConnection>(remoteConnections);
        }

        public ReadOnlyCollection<RemotePeer> RemotePeers { get; }
        public ReadOnlyCollection<NetConnection> RemoteConnections { get; }

        public void AddRemotePeer(RemotePeer peer)
        {
            remotePeers.Add(peer);
            remoteConnections.Add(peer.Connection);
        }

        public void RemoveRemotePeer(RemotePeer peer)
        {
            remotePeers.Remove(peer);
            remoteConnections.Remove(peer.Connection);
        }

        public override bool HasShutdown { get { return localPeer.HasShutdown; } }

        public override ISessionBackendListener Listener
        {
            get { return localPeer.Listener; }
            set { localPeer.Listener = value; }
        }

        public override Peer LocalPeer { get { return localPeer; } }

        public override TimeSpan SimulatedLatency
        {
            get { return localPeer.SimulatedLatency; }
            set { localPeer.SimulatedLatency = value; }
        }

        public override float SimulatedPacketLoss
        {
            get { return localPeer.SimulatedPacketLoss; }
            set { localPeer.SimulatedPacketLoss = value; }
        }

        public override int BytesPerSecondReceived { get; set; }
        public override int BytesPerSecondSent { get; set; }

        public override void Introduce(Peer client, Peer target)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }
            if (target == null)
            {
                throw new ArgumentNullException(nameof(target));
            }

            var remoteClient = client as RemotePeer;
            var remoteTarget = target as RemotePeer;

            if (remoteClient == null || remoteTarget == null)
            {
                throw new InvalidOperationException("Both client and target must be remote");
            }

            localPeer.Introduce(remoteClient, remoteTarget);
        }

        public LidgrenPeer FindPeerById(long id)
        {
            if (localPeer.SessionId == id)
            {
                return localPeer;
            }

            foreach (var remotePeer in remotePeers)
            {
                if (remotePeer.SessionId == id)
                {
                    return remotePeer;
                }
            }

            return null;
        }

        public override Peer FindRemotePeerByEndPoint(PeerEndPoint endPoint)
        {
            foreach (var remotePeer in remotePeers)
            {
                if (remotePeer.EndPoint.Equals(endPoint))
                {
                    return remotePeer;
                }
            }

            return null;
        }

        public override bool IsConnectedToEndPoint(PeerEndPoint endPoint)
        {
            return FindRemotePeerByEndPoint(endPoint) != null;
        }

        public override OutgoingMessage GetMessage(Peer recipient, SendDataOptions options, int channel)
        {
            var msg = outgoingMessagePool.Get();
            msg.recipient = recipient;
            msg.options = options;
            msg.channel = channel;
            return msg;
        }

        public override void SendMessage(OutgoingMessage message)
        {
            var msg = message as LidgrenOutgoingMessage;
            if (msg == null)
            {
                throw new NetworkException("Not possible to mix backends");
            }

            // Send to remote peer(s) if recipient is not the local peer only
            if (msg.Recipient != localPeer)
            {
                localPeer.SendMessage(msg);
            }

            // Send to self if recipient is null or the local peer
            if (msg.Recipient == null || msg.Recipient == localPeer)
            {
                msg.Buffer.Position = 0;

                // Pretend that the message was sent to the local peer over the network
                var incomingMsg = incomingMessagePool.Get();
                incomingMsg.Set(this, msg.Buffer);
                Listener.ReceiveMessage(incomingMsg, localPeer);
                incomingMessagePool.Recycle(incomingMsg);
            }

            outgoingMessagePool.Recycle(msg);
        }

        public override void Update()
        {
            localPeer.ReceiveMessages(outgoingMessagePool, incomingMessagePool);

            if (localPeer.HasShutdown)
            {
                return;
            }

            UpdateMasterServerRegistration();

            UpdateStatistics();
        }

        protected void UpdateMasterServerRegistration()
        {
            if (!Listener.IsDiscoverableOnline)
            {
                return;
            }

            var currentTime = DateTime.Now;
            var elapsedTime = currentTime - lastMasterServerRegistration;

            if (elapsedTime >= NetworkSessionSettings.MasterServerRegistrationInterval)
            {
                localPeer.RegisterWithMasterServer(outgoingMessagePool);

                lastMasterServerRegistration = currentTime;
            }
        }

        protected void UpdateStatistics()
        {
            var currentTime = DateTime.Now;
            int receivedBytes = localPeer.TotalReceivedBytes;
            int sentBytes = localPeer.TotalSentBytes;
            double elapsedSeconds = (currentTime - lastStatisticsUpdate).TotalSeconds;

            if (elapsedSeconds >= 1.0)
            {
                BytesPerSecondReceived = (int)Math.Round((receivedBytes - lastReceivedBytes) / elapsedSeconds);
                BytesPerSecondSent = (int)Math.Round((sentBytes - lastSentBytes) / elapsedSeconds);

                lastStatisticsUpdate = currentTime;
                lastReceivedBytes = receivedBytes;
                lastSentBytes = sentBytes;
            }
        }

        public override void Shutdown(string byeMessage)
        {
            if (localPeer.HasShutdown)
            {
                return;
            }

            localPeer.Disconnect(byeMessage);
        }
    }
}