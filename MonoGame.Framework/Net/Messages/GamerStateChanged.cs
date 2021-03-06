﻿using Microsoft.Xna.Framework.Net.Backend;
using System.Diagnostics;

namespace Microsoft.Xna.Framework.Net.Messages
{
    internal class GamerStateChanged : InternalMessage
    {
        public GamerStateChanged() : base(InternalMessageIndex.GamerStateChanged)
        { }

        public void Create(LocalNetworkGamer localGamer, bool sendNames, bool sendFlags, NetworkMachine recipient)
        {
            Debug.WriteLine($"Sending {Index} to {CurrentMachine.Session.MachineOwnerName(recipient)}...");
            var msg = Backend.GetMessage(recipient?.peer, SendDataOptions.ReliableInOrder, 1);
            msg.Write((byte)InternalMessageIndex.GamerStateChanged);

            msg.Write(localGamer.Id);

            msg.Write(sendNames);
            if (sendNames)
            {
                msg.Write(localGamer.DisplayName);
                msg.Write(localGamer.Gamertag);
            }

            msg.Write(sendFlags);
            if (sendFlags)
            {
                msg.Write(localGamer.IsPrivateSlot);
                msg.Write(localGamer.IsReady);
            }

            Queue.Place(msg);
        }

        public override void Receive(BaseIncomingMessage msg, NetworkMachine senderMachine)
        {
            if (senderMachine.IsLocal)
            {
                return;
            }

            byte id = msg.ReadByte();
            var remoteGamer = CurrentMachine.Session.FindGamerById(id);

            if (remoteGamer == null || remoteGamer.Machine != senderMachine)
            {
                // TODO: SuspiciousUnexpectedMessage
                Debug.Assert(false);
                return;
            }

            bool readNames = msg.ReadBoolean();
            if (readNames)
            {
                remoteGamer.DisplayName = msg.ReadString();
                remoteGamer.Gamertag = msg.ReadString();
            }

            bool readFlags = msg.ReadBoolean();
            if (readFlags)
            {
                remoteGamer.IsPrivateSlot = msg.ReadBoolean();
                remoteGamer.SetReadyState(msg.ReadBoolean()); // TODO: Discard ready=true if session state is Playing? Report for cheating (and if host, kick)?
            }
        }
    }
}
