using System;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NatTest.Common;
using Org.BouncyCastle.Crypto.Parameters;

namespace NatTest.Client
{
    internal sealed class HostedGameClient : INatPunchListener
    {
        internal delegate void ClientErrorHandler(string message);
        internal event ClientErrorHandler OnErrorOccurred;
        internal delegate void ClientInfoHandler(string message);
        internal ClientInfoHandler OnInfo { get; set; }

        private const string ProtocolKey = "NATTEST/1";
        private const string IntroducerAcknowledgment = "NATTEST/1|INTRODUCER_ACK";
        private const string IntroducerErrorPrefix = "NATTEST/1|INTRODUCER_ERR|";

        private readonly IPEndPoint introducer;
        private readonly int localPort;
        private readonly int connectTimeout;
        private readonly int introduceTimeout;
        private readonly string token;
        private readonly MatchmakingTokenClaims clientClaims;
        private readonly ECPublicKeyParameters introducerKey;
        private readonly Guid expectedHostPlayerId;

        private NetManager manager;
        private volatile bool connected;
        private volatile bool stop;
        private volatile bool introduced;
        private bool introducerReachable;
        private bool attempted;
        private DateTime connectDeadline;

        // clientClaims are read from the client's own token; expectedHostPlayerId (the listing's hostPlayerId) is optional.
        public HostedGameClient(IPEndPoint introducer, int localPort, int connectTimeout, int introduceTimeout, string token,
            MatchmakingTokenClaims clientClaims, ECPublicKeyParameters introducerKey, Guid expectedHostPlayerId)
        {
            this.introducer = introducer;
            this.localPort = localPort;
            this.connectTimeout = connectTimeout;
            this.introduceTimeout = introduceTimeout;
            this.token = token;
            this.clientClaims = clientClaims;
            this.introducerKey = introducerKey;
            this.expectedHostPlayerId = expectedHostPlayerId;
        }

        public void Run()
        {
            var listener = new EventBasedNetListener();
            manager = new NetManager(listener);
            manager.NatPunchEnabled = true;
            manager.UnconnectedMessagesEnabled = true;
            manager.DisconnectTimeout = connectTimeout * 1000;

            listener.NetworkReceiveUnconnectedEvent += (remote, reader, messageType) =>
            {
                try
                {
                    string message = reader.GetString(128);
                    if (!remote.Equals(introducer)) return;
                    if (message == IntroducerAcknowledgment && !introducerReachable)
                    {
                        introducerReachable = true;
                        ReportInfo("Introducer acknowledged the client request.");
                    }
                    else if (message.StartsWith(IntroducerErrorPrefix, StringComparison.Ordinal))
                    {
                        introducerReachable = true;
                        ReportError("Introducer rejected the client request: " + message.Substring(IntroducerErrorPrefix.Length) + ".");
                        stop = true;
                    }
                }
                finally { reader.Recycle(); }
            };

            listener.PeerConnectedEvent += peer =>
            {
                ReportInfo("Direct peer connected: " + peer.Address + ":" + peer.Port);
                connected = true;
                var writer = new NetDataWriter();
                writer.Put("hello");
                peer.Send(writer, DeliveryMethod.ReliableOrdered);
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                string reason = "";
                try
                {
                    if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0) reason = " (" + info.AdditionalData.GetString(64) + ")";
                }
                catch (Exception) { }
                ReportError("Disconnected from " + peer.Address + ":" + peer.Port + ": " + info.Reason + reason);
                connected = false;
            };

            manager.NatPunchModule.Init(this);
            if (!manager.Start(localPort))
            {
                manager = null;
                throw new InvalidOperationException("Cannot bind local UDP port " + localPort);
            }

            ReportInfo("Client UDP port " + localPort + " for session " + clientClaims.SessionId + " as player " + clientClaims.PlayerId + "; requesting introduction via " + introducer + ".");
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                eventArgs.Cancel = true;
                stop = true;
                manager.Stop();
            };

            try
            {
                DateTime next = DateTime.MinValue;
                DateTime introduceDeadline = DateTime.UtcNow.AddSeconds(introduceTimeout);

                while (manager.IsRunning && !stop)
                {
                    manager.PollEvents();
                    manager.NatPunchModule.PollEvents();

                    if (!introduced && !connected && DateTime.UtcNow >= next)
                    {
                        manager.NatPunchModule.SendNatIntroduceRequest(
                            introducer.Address.ToString(),
                            introducer.Port,
                            token);
                        next = DateTime.UtcNow.AddSeconds(3);
                    }

                    if (!introduced && DateTime.UtcNow > introduceDeadline)
                    {
                        ReportError(introducerReachable
                            ? "The introducer acknowledged the request, but no NAT introduction succeeded before the timeout."
                            : "The introducer did not acknowledge the request before the timeout; check its address, port, and UDP reachability.");
                        stop = true;
                        break;
                    }

                    if (introduced && !connected && DateTime.UtcNow > connectDeadline)
                    {
                        ReportError("Introduction succeeded, but direct peer connection timed out; check peer firewall and NAT behavior.");
                        break;
                    }

                    Thread.Sleep(15);
                }
            }
            finally
            {
                manager.Stop();
                manager = null;
            }

            ReportInfo("Client stopped.");
        }

        public void OnNatIntroductionRequest(IPEndPoint local, IPEndPoint remote, string token)
        {
            ReportInfo("Received NAT introduction request from " + remote + " token=" + token);
        }

        public void OnNatIntroductionFailed(IPEndPoint endpoint, string token)
        {
            ReportError("NAT introduction failed for " + endpoint + ".");
        }

        // The token here is the introducer's introduction token relayed in the host's punch packet. Anyone can send a punch,
        // so only connect when the introducer signed this pairing of our session, our player, and the expected host.
        public void OnNatIntroductionSuccess(IPEndPoint endpoint, NatAddressType type, string introductionToken)
        {
            string rejection = CheckIntroduction(introductionToken);
            if (rejection != null)
            {
                ReportError("Ignored introduction from " + endpoint + " (" + type + "): " + rejection + ".");
                return;
            }

            if (!introduced) connectDeadline = DateTime.UtcNow.AddSeconds(connectTimeout);
            introduced = true;
            ReportInfo("Introduction success: " + endpoint + " (" + type + ")");

            if (type == NatAddressType.External && !attempted && manager.ConnectedPeersCount == 0)
            {
                attempted = true;
                // The host authorizes the connection with our MM client token.
                var connectData = new NetDataWriter();
                connectData.Put(ProtocolKey);
                connectData.Put(token);
                manager.Connect(endpoint, connectData);
            }
        }

        private string CheckIntroduction(string introductionToken)
        {
            IntroductionTokenClaims claims;
            string error;
            if (!NatTokens.TryVerifyIntroductionToken(introductionToken, introducerKey, NatTokens.NowUnixSeconds(), out claims, out error)) return error;
            if (claims.SessionId != clientClaims.SessionId) return "session-mismatch";
            if (claims.ClientPlayerId != clientClaims.PlayerId) return "client-player-mismatch";
            if (expectedHostPlayerId != Guid.Empty && claims.HostPlayerId != expectedHostPlayerId) return "host-player-mismatch";
            return null;
        }

        private void ReportError(string message)
        {
            var handlers = OnErrorOccurred;
            if (handlers == null) return;
            foreach (ClientErrorHandler handler in handlers.GetInvocationList())
            {
                try { handler(message); }
                catch (Exception exception) { Console.Error.WriteLine("Client error subscriber failed: " + exception.Message); }
            }
        }

        private void ReportInfo(string message)
        {
            var handler = OnInfo;
            if (handler == null) return;
            try { handler(message); }
            catch (Exception exception) { Console.Error.WriteLine("Client info subscriber failed: " + exception.Message); }
        }
    }
}
