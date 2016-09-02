﻿using Lidgren.Network;
using System;
using System.Diagnostics;

namespace Microsoft.Xna.Framework.Net.Messages
{
    internal struct GamerJoinRequestMessageSender : IInternalMessageSender
    {
        public InternalMessageType MessageType { get { return InternalMessageType.GamerJoinRequest; } }
        public int SequenceChannel { get { return 1; } }
        public SendDataOptions Options { get { return SendDataOptions.ReliableInOrder; } }

        public void Send(NetBuffer output, NetworkMachine currentMachine)
        { }
    }

    internal struct GamerJoinRequestMessageReceiver : IInternalMessageReceiver
    {
        public void Receive(NetBuffer input, NetworkMachine currentMachine, NetworkMachine senderMachine)
        {
            if (!currentMachine.IsHost)
            {
                Debug.WriteLine("Warning: Received GamerJoinRequest when not host!");
                return;
            }

            NetworkSession.Session.Send(new GamerJoinResponseMessageSender(), senderMachine);
        }
    }
}