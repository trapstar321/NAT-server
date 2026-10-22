using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using LiteNetLib;
using LiteNetLib.Utils;

namespace NatTest.Tests.EndToEnd
{
    // An in-process LiteNetLib peer that polls its events on a background thread. Used to act as a misbehaving client or
    // introducer that the real programs must reject.
    internal sealed class LitePeer : IDisposable
    {
        internal readonly EventBasedNetListener Listener = new EventBasedNetListener();
        internal readonly NetManager Manager;
        private readonly Thread pump;
        private volatile bool running = true;

        internal LitePeer(INatPunchListener natListener = null)
        {
            Manager = new NetManager(Listener) { NatPunchEnabled = true, UnconnectedMessagesEnabled = true };
            if (natListener != null) Manager.NatPunchModule.Init(natListener);
            if (!Manager.Start(IPAddress.Loopback, IPAddress.IPv6Loopback, 0)) throw new InvalidOperationException("Cannot bind a test UDP port.");
            pump = new Thread(() =>
            {
                while (running)
                {
                    Manager.PollEvents();
                    Manager.NatPunchModule.PollEvents();
                    Thread.Sleep(10);
                }
            }) { IsBackground = true };
            pump.Start();
        }

        internal int Port { get { return Manager.LocalPort; } }

        // Connects directly to a host, bypassing NAT introduction. Returns null when connected, otherwise the rejection reason
        // the host sent back. Keep the peer alive to hold the connection open.
        internal async Task<string> Connect(int hostPort, string protocol, string token)
        {
            var result = new TaskCompletionSource<string>();
            Listener.PeerConnectedEvent += peer => result.TrySetResult(null);
            Listener.PeerDisconnectedEvent += (peer, info) =>
            {
                string reason = info.Reason.ToString();
                if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0) reason = info.AdditionalData.GetString(64);
                result.TrySetResult(reason);
            };

            var data = new NetDataWriter();
            if (protocol != null) data.Put(protocol);
            if (token != null) data.Put(token);
            Manager.Connect(new IPEndPoint(IPAddress.Loopback, hostPort), data);
            if (await Task.WhenAny(result.Task, Task.Delay(TimeSpan.FromSeconds(10))) != result.Task) return "no-response";
            return await result.Task;
        }

        // Sends a raw NAT introduction request and returns the introducer's first unconnected reply.
        internal async Task<string> SendIntroduceRequest(int introducerPort, string token)
        {
            var reply = new TaskCompletionSource<string>();
            Listener.NetworkReceiveUnconnectedEvent += (remote, reader, type) =>
            {
                reply.TrySetResult(reader.GetString(128));
                reader.Recycle();
            };
            for (int attempt = 0; attempt < 10 && !reply.Task.IsCompleted; attempt++)
            {
                Manager.NatPunchModule.SendNatIntroduceRequest("127.0.0.1", introducerPort, token);
                await Task.WhenAny(reply.Task, Task.Delay(500));
            }
            return reply.Task.IsCompleted ? await reply.Task : "no-response";
        }

        public void Dispose()
        {
            running = false;
            pump.Join(1000);
            Manager.Stop();
        }
    }

    // A fake introducer: answers every client request by introducing it to the real host with whatever token the test
    // supplies, so the test controls exactly what reaches the client's OnNatIntroductionSuccess.
    internal sealed class RogueIntroducer : INatPunchListener, IDisposable
    {
        private readonly LitePeer peer;
        private readonly IPEndPoint host;

        internal RogueIntroducer(int hostPort)
        {
            host = new IPEndPoint(IPAddress.Loopback, hostPort);
            peer = new LitePeer(this);
        }

        internal int Port { get { return peer.Port; } }

        // Called with the requesting client's MM token; returns the introduction token to hand out.
        internal Func<string, string> IntroductionToken { get; set; }

        public void OnNatIntroductionRequest(IPEndPoint localEndPoint, IPEndPoint remoteEndPoint, string token)
        {
            peer.Manager.NatPunchModule.NatIntroduce(host, host, localEndPoint, remoteEndPoint, IntroductionToken(token));
        }

        public void OnNatIntroductionSuccess(IPEndPoint targetEndPoint, NatAddressType type, string token) { }

        public void OnNatIntroductionFailed(IPEndPoint targetEndPoint, string token) { }

        public void Dispose() { peer.Dispose(); }
    }
}
