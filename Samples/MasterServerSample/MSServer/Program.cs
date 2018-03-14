using System;
using System.Collections.Generic;

using Lidgren.Network;
using MSCommon;
using System.Net;
using Phoesion.Network;

namespace MSServer
{
    class Program
    {
        static void Main(string[] args)
        {
            IPEndPoint masterServerEndpoint = NetUtility.Resolve("localhost", CommonConstants.MasterServerPort);

            NetPeerConfiguration config = new NetPeerConfiguration("game");
            config.SetMessageTypeEnabled(NetIncomingMessageType.UnconnectedData, true);
            config.SetMessageTypeEnabled(NetIncomingMessageType.NatIntroductionSuccess, true);
            config.Port = CommonConstants.GameServerPort;

            NetServer server = new NetServer(config);
            server.Start();

            Console.WriteLine("Server started; waiting 5 seconds...");
            System.Threading.Thread.Sleep(5000);

            var lastRegistered = -60.0f;

            while (Console.KeyAvailable == false || Console.ReadKey().Key != ConsoleKey.Escape)
            {
                // (re-)register periodically with master server
                if (NetTime.Now > lastRegistered + 60)
                {
                    // register with master server
                    NetOutgoingMessage regMsg = server.CreateMessage();
                    regMsg.Write((byte)MasterServerMessageType.RegisterHost);
                    IPAddress mask;
                    IPAddress adr = NetUtility.GetMyAddress(out mask);
                    regMsg.Write(server.UniqueIdentifier);
                    regMsg.Write(new IPEndPoint(adr, CommonConstants.GameServerPort));
                    Console.WriteLine("Sending registration to master server");
                    server.SendUnconnectedMessage(regMsg, masterServerEndpoint);
                    lastRegistered = (float)NetTime.Now;
                }

                NetIncomingMessage inc;
                while ((inc = server.ReadMessage()) != null)
                {
                    switch (inc.MessageType)
                    {
                        case NetIncomingMessageType.VerboseDebugMessage:
                        case NetIncomingMessageType.DebugMessage:
                        case NetIncomingMessageType.WarningMessage:
                        case NetIncomingMessageType.ErrorMessage:
                            Console.WriteLine(inc.ReadString());
                            break;

                        case NetIncomingMessageType.UnconnectedData:
                            switch ((MasterServerMessageType)inc.ReadByte())
                            {
                                case MasterServerMessageType.Heil:
                                    Console.WriteLine("Received hello from client");
                                    break;

                                default:
                                    break;
                            }
                            break;
                    }
                }

                System.Threading.Thread.Sleep(1);
            }

            Console.ReadKey();
        }
    }
}
