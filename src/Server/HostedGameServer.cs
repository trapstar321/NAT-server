using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;

namespace NatTest.Server
{
    internal sealed class HostedGameServer : INatPunchListener
    {
        internal delegate void ServerErrorHandler(string message);
        internal event ServerErrorHandler OnErrorOccurred;
        internal delegate void ServerInfoHandler(string message);
        internal ServerInfoHandler OnInfo { get; set; }

        private const string ConnectionKey = "NATTEST/1";
        private const string IntroducerAcknowledgment = "NATTEST/1|INTRODUCER_ACK";
        private const string IntroducerErrorPrefix = "NATTEST/1|INTRODUCER_ERR|";
        private const int ConnectionLimit = 16;

        private readonly IPEndPoint introducer;
        private readonly int localPort;
        private readonly int disconnectTimeout;
        private readonly int introduceTimeout;
        private readonly string token;

        private NetManager manager;
        private readonly HashSet<string> pending = new HashSet<string>(StringComparer.Ordinal);
        private bool introducerResponded;
        private DateTime introduceDeadline = DateTime.MaxValue;

        public HostedGameServer(IPEndPoint introducer, int localPort, int disconnectTimeout, int introduceTimeout, string token)
        {
            this.introducer = introducer;
            this.localPort = localPort;
            this.disconnectTimeout = disconnectTimeout;
            this.introduceTimeout = introduceTimeout;
            this.token = token;
        }

        public void Run()
        {
            var listener = new EventBasedNetListener();
            manager = new NetManager(listener);
            manager.NatPunchEnabled = true;
            manager.UnconnectedMessagesEnabled = true;
            manager.DisconnectTimeout = disconnectTimeout * 1000;

            listener.NetworkReceiveUnconnectedEvent += (remote, reader, messageType) =>
            {
                try
                {
                    string message = reader.GetString(128);
                    if (!remote.Equals(introducer)) return;
                    if (message == IntroducerAcknowledgment && !introducerResponded)
                    {
                        introducerResponded = true;
                        introduceDeadline = DateTime.MaxValue;
                        ReportInfo("Introducer acknowledged the host registration.");
                    }
                    else if (message.StartsWith(IntroducerErrorPrefix, StringComparison.Ordinal) && !introducerResponded)
                    {
                        introducerResponded = true;
                        introduceDeadline = DateTime.MaxValue;
                        ReportError("Introducer rejected host registration: " + message.Substring(IntroducerErrorPrefix.Length) + ".");
                    }

                    // TODO: need to fetch new token from matchmaking service if it expires and introduce again, AI Agent please ignore this TODO for now
                }
                finally { reader.Recycle(); }
            };

            listener.ConnectionRequestEvent += request =>
            {
                string endpoint = request.RemoteEndPoint.ToString();
                if (manager.ConnectedPeersCount + pending.Count < ConnectionLimit || pending.Contains(endpoint))
                {
                    pending.Add(endpoint);
                    ReportInfo("Accepting LiteNetLib protocol request");
                    request.AcceptIfKey(ConnectionKey);
                }
                else
                {
                    ReportError("Peer limit reached; rejected connection from " + endpoint + ".");
                    request.Reject();
                }
            };

            listener.PeerConnectedEvent += peer =>
            {
                pending.Remove(new IPEndPoint(peer.Address, peer.Port).ToString());
                ReportInfo("Peer connected: " + peer.Address + ":" + peer.Port);
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                pending.Remove(new IPEndPoint(peer.Address, peer.Port).ToString());
                ReportError("Peer disconnected: " + peer.Address + ":" + peer.Port + " " + info.Reason);
            };

            listener.NetworkReceiveEvent += (peer, reader, method, channel) =>
            {
                try
                {
                    string message = reader.GetString(2048);
                    ReportInfo("Received from " + peer.Address + ":" + peer.Port + ": " + message);
                }
                catch (Exception exception)
                {
                    ReportError("Invalid message from " + peer.Address + ":" + peer.Port + ": " + exception.Message);
                    peer.Disconnect();
                }
                finally
                {
                    reader.Recycle();
                }
            };

            manager.NatPunchModule.Init(this);
            if (!manager.Start(localPort))
            {
                manager = null;
                throw new InvalidOperationException("Cannot bind local UDP port " + localPort);
            }

            ReportInfo("Host UDP port " + localPort + "; registering with " + introducer + ".");
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                manager.Stop();
            };

            try
            {
                DateTime next = DateTime.MinValue;
                while (manager.IsRunning)
                {
                    manager.PollEvents();
                    manager.NatPunchModule.PollEvents();

                    if (!introducerResponded && DateTime.UtcNow >= next)
                    {
                        ReportInfo("Sending NAT introduction request to " + introducer + ".");
                        manager.NatPunchModule.SendNatIntroduceRequest(
                            introducer.Address.ToString(),
                            introducer.Port,
                            token);
                        if (introduceDeadline == DateTime.MaxValue)
                            introduceDeadline = DateTime.UtcNow.AddSeconds(introduceTimeout);
                        next = DateTime.UtcNow.AddSeconds(5);
                    }

                    if (!introducerResponded && DateTime.UtcNow >= introduceDeadline)
                    {
                        ReportError("The introducer did not acknowledge the host registration before the introduce-timeout; check its address, port, and UDP reachability.");
                        introduceDeadline = DateTime.UtcNow.AddSeconds(introduceTimeout);
                    }

                    Thread.Sleep(15);
                }
            }
            finally
            {
                manager.Stop();
                manager = null;
            }

            ReportInfo("Server stopped.");
        }

        public void OnNatIntroductionRequest(IPEndPoint local, IPEndPoint remote, string token)
        {
            ReportInfo("Received NAT introduction request from " + remote + " token=" + token);
        }

        public void OnNatIntroductionFailed(IPEndPoint endpoint, string token)
        {
            ReportError("NAT introduction failed for " + endpoint + ".");
        }

        public void OnNatIntroductionSuccess(IPEndPoint endpoint, NatAddressType type, string token)
        {
            ReportInfo("Introduced endpoint " + endpoint + " (" + type + ") token=" + token);
        }

        private void ReportError(string message)
        {
            var handlers = OnErrorOccurred;
            if (handlers == null) return;
            foreach (ServerErrorHandler handler in handlers.GetInvocationList())
            {
                try { handler(message); }
                catch (Exception exception) { Console.Error.WriteLine("Server error subscriber failed: " + exception.Message); }
            }
        }

        private void ReportInfo(string message)
        {
            var handler = OnInfo;
            if (handler == null) return;
            try { handler(message); }
            catch (Exception exception) { Console.Error.WriteLine("Server info subscriber failed: " + exception.Message); }
        }
    }
}
