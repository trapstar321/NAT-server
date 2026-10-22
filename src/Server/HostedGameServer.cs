using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using LiteNetLib;
using LiteNetLib.Utils;
using NatTest.Common;
using Org.BouncyCastle.Crypto.Parameters;

namespace NatTest.Server
{
    internal sealed class HostedGameServer : INatPunchListener
    {
        internal delegate void ServerErrorHandler(string message);
        internal event ServerErrorHandler OnErrorOccurred;
        internal delegate void ServerInfoHandler(string message);
        internal ServerInfoHandler OnInfo { get; set; }

        private const string ProtocolKey = "NATTEST/1";
        private const string IntroducerAcknowledgment = "NATTEST/1|INTRODUCER_ACK";
        private const string IntroducerErrorPrefix = "NATTEST/1|INTRODUCER_ERR|";
        private const int ConnectionLimit = 16;

        private readonly IPEndPoint introducer;
        private readonly int localPort;
        private readonly int disconnectTimeout;
        private readonly int introduceTimeout;
        private readonly string token;
        private readonly MatchmakingTokenClaims hostClaims;
        private readonly ECPublicKeyParameters matchmakingKey;
        private readonly ECPublicKeyParameters introducerKey;

        private NetManager manager;
        // Endpoint -> player ID for pending and connected peers, so each player has at most one connection.
        private readonly Dictionary<string, Guid> players = new Dictionary<string, Guid>(StringComparer.Ordinal);
        private bool introducerResponded;
        private DateTime introduceDeadline = DateTime.MaxValue;

        // hostClaims must come from the verified host token; the session and host player ID are taken from it.
        public HostedGameServer(IPEndPoint introducer, int localPort, int disconnectTimeout, int introduceTimeout, string token,
            MatchmakingTokenClaims hostClaims, ECPublicKeyParameters matchmakingKey, ECPublicKeyParameters introducerKey)
        {
            this.introducer = introducer;
            this.localPort = localPort;
            this.disconnectTimeout = disconnectTimeout;
            this.introduceTimeout = introduceTimeout;
            this.token = token;
            this.hostClaims = hostClaims;
            this.matchmakingKey = matchmakingKey;
            this.introducerKey = introducerKey;
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

            listener.ConnectionRequestEvent += OnConnectionRequest;

            listener.PeerConnectedEvent += peer =>
            {
                ReportInfo("Peer connected: " + peer.Address + ":" + peer.Port);
            };

            listener.PeerDisconnectedEvent += (peer, info) =>
            {
                players.Remove(new IPEndPoint(peer.Address, peer.Port).ToString());
                ReportError("Peer disconnected: " + peer.Address + ":" + peer.Port + " " + info.Reason);
            };

            listener.NetworkReceiveEvent += (peer, reader, method, channel) =>
            {
                try
                {
                    string message = reader.GetString(2048);
                    ReportInfo("Received from player " + peer.Tag + " at " + peer.Address + ":" + peer.Port + ": " + message);
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

            ReportInfo("Host UDP port " + localPort + " for session " + hostClaims.SessionId + " as player " + hostClaims.PlayerId + "; registering with " + introducer + ".");
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

        // The connecting client is authorized by its MM client token in OnConnectionRequest; this check only reports
        // which player the introducer paired with this host, and flags punches that did not come from a real introduction.
        public void OnNatIntroductionSuccess(IPEndPoint endpoint, NatAddressType type, string token)
        {
            IntroductionTokenClaims claims;
            string error;
            if (!NatTokens.TryVerifyIntroductionToken(token, introducerKey, NatTokens.NowUnixSeconds(), out claims, out error))
                ReportError("Ignored introduction from " + endpoint + " (" + type + "): " + error + ".");
            else if (claims.SessionId != hostClaims.SessionId || claims.HostPlayerId != hostClaims.PlayerId)
                ReportError("Ignored introduction from " + endpoint + " (" + type + "): introduction-mismatch.");
            else
                ReportInfo("Verified introduction of player " + claims.ClientPlayerId + " at " + endpoint + " (" + type + ").");
        }

        // The client sends the protocol key followed by its MM client token. MM only issues that token after authorizing the
        // join (including the private-game secret), so the host does not need the game secret.
        private void OnConnectionRequest(ConnectionRequest request)
        {
            string endpoint = request.RemoteEndPoint.ToString();
            string reason = AuthorizeConnection(request, endpoint);
            if (reason != null)
            {
                ReportError("Rejected connection from " + endpoint + ": " + reason + ".");
                var writer = new NetDataWriter();
                writer.Put(reason);
                request.Reject(writer);
                return;
            }

            Guid player = players[endpoint];
            NetPeer peer = request.Accept();
            if (peer != null) peer.Tag = player;
            ReportInfo("Accepted player " + player + " from " + endpoint + ".");
        }

        private string AuthorizeConnection(ConnectionRequest request, string endpoint)
        {
            string protocol, clientToken;
            try
            {
                protocol = request.Data.GetString(32);
                clientToken = request.Data.GetString(NatTokens.LiteNetLibMaxTokenLength);
            }
            catch (Exception) { return "malformed-request"; }
            if (protocol != ProtocolKey) return "protocol-mismatch";

            MatchmakingTokenClaims claims;
            string error;
            if (!NatTokens.TryVerifyMatchmakingToken(clientToken, matchmakingKey, NatTokens.NowUnixSeconds(), out claims, out error)) return error;
            if (claims.Role != NatTokens.ClientRole) return "client-token-required";
            if (claims.SessionId != hostClaims.SessionId) return "session-mismatch";

            Guid existing;
            if (players.TryGetValue(endpoint, out existing)) return existing == claims.PlayerId ? null : "endpoint-in-use";
            if (players.ContainsValue(claims.PlayerId)) return "player-already-connected";
            if (players.Count >= ConnectionLimit) return "peer-limit-reached";
            players[endpoint] = claims.PlayerId;
            return null;
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
