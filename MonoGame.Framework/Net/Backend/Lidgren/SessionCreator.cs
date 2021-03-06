﻿using System;
using System.Net;
using System.Collections.Generic;
using System.Threading;
using System.Diagnostics;
using Microsoft.Xna.Framework.GamerServices;

using Lidgren.Network;

namespace Microsoft.Xna.Framework.Net.Backend.Lidgren
{
    internal class SessionCreator : BaseSessionCreator
    {
        private const int DiscoveryTimeMs = 4000;
        private const int FullyConnectedPollingTimeMs = 50;
        private const int FullyConnectedTimeOutMs = 4000;

        private static bool WaitUntilFullyConnected(NetworkSession session)
        {
            int totalTime = 0;

            while (!session.IsFullyConnected)
            {
                if (totalTime > FullyConnectedTimeOutMs)
                {
                    return false;
                }

                session.SilentUpdate();

                Thread.Sleep(FullyConnectedPollingTimeMs);
                totalTime += FullyConnectedPollingTimeMs;
            }

            return true;
        }

        public override NetworkSession Create(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, int maxGamers, int privateGamerSlots, NetworkSessionProperties sessionProperties)
        {
            var config = new NetPeerConfiguration(NetworkSessionSettings.GameAppId);
            config.Port = NetworkSessionSettings.Port;
            config.AcceptIncomingConnections = true;
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest);
            config.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

            var peer = new NetPeer(config);
            try
            {
                peer.Start();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Lidgren error", e);
            }
            Debug.WriteLine("Peer started.");

            var session = new NetworkSession(new SessionBackend(peer), new BasePeerEndPoint[0], true, maxGamers, privateGamerSlots, sessionType, sessionProperties, localGamers);

            if (!WaitUntilFullyConnected(session))
            {
                session.Dispose();

                throw new NetworkException("Could not initialize session");
            }
            Debug.WriteLine("Peer fully connected.");

            return session;
        }

        private void AddAvailableNetworkSession(BasePeerEndPoint endPoint, NetworkSessionPublicInfo publicInfo, IEnumerable<SignedInGamer> localGamers, NetworkSessionType searchType, NetworkSessionProperties searchProperties, IList<AvailableNetworkSession> availableSessions, IPEndPoint localIp = null)
        {
            if (searchType == publicInfo.sessionType && searchProperties.SearchMatch(publicInfo.sessionProperties))
            {
                var availableSession = new AvailableNetworkSession(endPoint, localGamers, publicInfo.maxGamers, publicInfo.privateGamerSlots, publicInfo.sessionType, publicInfo.currentGamerCount, publicInfo.hostGamertag, publicInfo.openPrivateGamerSlots, publicInfo.openPublicGamerSlots, publicInfo.sessionProperties)
                {
                    Tag = localIp,
                };
                availableSessions.Add(availableSession);
            }
        }

        public override AvailableNetworkSessionCollection Find(NetworkSessionType sessionType, IEnumerable<SignedInGamer> localGamers, NetworkSessionProperties searchProperties)
        {
            var masterServerEndPoint = NetUtility.Resolve(NetworkSessionSettings.MasterServerAddress, NetworkSessionSettings.MasterServerPort);

            var config = new NetPeerConfiguration(NetworkSessionSettings.GameAppId);
            config.Port = 0;
            config.AcceptIncomingConnections = false;
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            config.EnableMessageType(NetIncomingMessageType.UnconnectedData);
            config.EnableMessageType(NetIncomingMessageType.DiscoveryResponse);

            var discoverPeer = new NetPeer(config);
            try
            {
                discoverPeer.Start();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Lidgren error", e);
            }
            Debug.WriteLine("Discovery peer started.");

            // Send discovery request
            if (sessionType == NetworkSessionType.SystemLink)
            {
                Debug.WriteLine("Sending local discovery request...");

                discoverPeer.DiscoverLocalPeers(NetworkSessionSettings.Port);
            }
            else if (sessionType == NetworkSessionType.PlayerMatch || sessionType == NetworkSessionType.Ranked)
            {
                Debug.WriteLine("Sending discovery request to master server...");

                MasterServer.RequestHosts(discoverPeer);
            }
            else
            {
                throw new InvalidOperationException();
            }

            // Wait for answers
            Thread.Sleep(DiscoveryTimeMs);

            // Get list of answers
            var availableSessions = new List<AvailableNetworkSession>();

            NetIncomingMessage rawMsg;
            while ((rawMsg = discoverPeer.ReadMessage()) != null)
            {
                if (rawMsg.MessageType == NetIncomingMessageType.UnconnectedData && !rawMsg.SenderEndPoint.Equals(masterServerEndPoint))
                {
                    Debug.WriteLine($"Unconnected data not from master server recieved from {rawMsg.SenderEndPoint}");
                    discoverPeer.Recycle(rawMsg);
                    continue;
                }

                if (rawMsg.MessageType == NetIncomingMessageType.DiscoveryResponse || rawMsg.MessageType == NetIncomingMessageType.UnconnectedData)
                {
                    MasterServer.ParseRequestHostsResponse(rawMsg, out GuidEndPoint endPoint, out NetworkSessionPublicInfo publicInfo);
                    var localIp = rawMsg.MessageType == NetIncomingMessageType.DiscoveryResponse ? rawMsg.SenderEndPoint : null;

                    AddAvailableNetworkSession(endPoint, publicInfo, localGamers, sessionType, searchProperties, availableSessions, localIp);
                }
                else
                {
                    switch (rawMsg.MessageType)
                    {
                        case NetIncomingMessageType.VerboseDebugMessage:
                        case NetIncomingMessageType.DebugMessage:
                        case NetIncomingMessageType.WarningMessage:
                        case NetIncomingMessageType.ErrorMessage:
                            Debug.WriteLine($"Lidgren: {rawMsg.ReadString()}");
                            break;
                        default:
                            Debug.WriteLine($"Unhandled type: {rawMsg.MessageType}");
                            break;
                    }
                }

                discoverPeer.Recycle(rawMsg);
            }
            discoverPeer.Shutdown("Discovery complete");
            Debug.WriteLine("Discovery peer shut down.");

            return new AvailableNetworkSessionCollection(availableSessions);
        }

        public override NetworkSession Join(AvailableNetworkSession availableSession)
        {
            var config = new NetPeerConfiguration(NetworkSessionSettings.GameAppId);
            config.Port = 0;
            config.AcceptIncomingConnections = true;
            config.EnableMessageType(NetIncomingMessageType.VerboseDebugMessage);
            //config.EnableMessageType(NetIncomingMessageType.DiscoveryRequest); // TODO: Host migration
            config.EnableMessageType(NetIncomingMessageType.NatIntroductionSuccess);
            config.EnableMessageType(NetIncomingMessageType.ConnectionApproval);

            var peer = new NetPeer(config);
            try
            {
                peer.Start();
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Lidgren error: " + e.Message, e);
            }
            Debug.WriteLine("Peer started.");

            var backend = new SessionBackend(peer);
            var session = new NetworkSession(backend,
                new BasePeerEndPoint[] { availableSession.HostEndPoint },
                false,
                availableSession.MaxGamers,
                availableSession.PrivateGamerSlots,
                availableSession.SessionType,
                availableSession.SessionProperties,
                availableSession.LocalGamers);

            var localPeer = (LocalPeer)backend.LocalPeer;
            if (availableSession.SessionType == NetworkSessionType.SystemLink)
            {
                if ((session as ISessionBackendListener).AllowConnectionToHostAsClient(availableSession.HostEndPoint))
                {
                    var hostIp = (IPEndPoint)availableSession.Tag;
                    var hostExternalIp = hostIp; // LAN is local anyway...
                    var observedExternalIp = localPeer.InternalIp; // LAN is local anyway...
                    localPeer.Connect(hostIp, hostExternalIp, observedExternalIp);
                }
            }
            else if (availableSession.SessionType == NetworkSessionType.PlayerMatch || availableSession.SessionType == NetworkSessionType.Ranked)
            {
                // Note: Actual Connect call is handled by NetworkSession once NAT introduction is successful
                MasterServer.RequestIntroduction(peer, (GuidEndPoint)availableSession.HostEndPoint, localPeer.InternalIp);
            }
            else
            {
                throw new InvalidOperationException();
            }

            if (!WaitUntilFullyConnected(session))
            {
                session.Dispose();

                throw new NetworkSessionJoinException("Could not fully connect to session");
            }
            Debug.WriteLine("Peer fully connected.");

            return session;
        }
    }
}
