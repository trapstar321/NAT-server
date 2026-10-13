# LiteNetLib NAT punch-through test

Three Windows console programs exercise LiteNetLib's built-in UDP NAT introduction: an introducer, a session host, and a client. The introducer exchanges observed endpoints and does not relay game messages.

## Compatibility and prerequisites

- LiteNetLib **1.3.5** is pinned. NuGet provides its `netstandard2.0` assembly; that is compatible with the shared library and .NET Framework 4.7.2. LiteNetLib 2.x requires .NET Standard 2.1 and is not selected for this Unity Mono target.
- `NatTest.Common` targets .NET Standard 2.0, C# 7.3. The three console programs target .NET Framework 4.7.2.
- Install .NET SDK (the repository was built with SDK 9/MSBuild 17.14), NuGet access on first restore, and the .NET Framework 4.7.2 Developer/Targeting Pack to build. Running requires .NET Framework 4.7.2 or later on Windows. This machine has reference packs through 4.8; the SDK restore supplies `Microsoft.NETFramework.ReferenceAssemblies` where needed.
- For Unity, add the LiteNetLib 1.3.5 source/package and reference `NatTest.Common` or port the networking classes; keep console entry points out of Unity. LiteNetLib lists Unity 2018.3+ support; verify against your Unity/Mono profile.

## Build

From this directory run:

```bat
build.bat
```

Equivalent restore/build commands are `dotnet restore NatTest.sln` and `dotnet build NatTest.sln -c Release`. `build.bat` publishes each net472 application into `artifacts\introducer`, `artifacts\server`, and `artifacts\client` and prints executable paths. Framework-dependent executables use the installed .NET Framework; they are not self-contained.

## Start a session

Make UDP 50010 reachable on the introducer machine. On a public VPS, allow inbound UDP 50010 in its network firewall and Windows Firewall. At home, forward UDP 50010 on the router to the introducer PC and allow it through Windows Firewall. Binding to `0.0.0.0` only listens on local interfaces; it does not make the service internet-reachable.

Run these in separate consoles (change `myhome.ddns.net` to your public hostname/IP):

```bat
artifacts\introducer\introducer.exe --bind 0.0.0.0 --port 50010
artifacts\server\server.exe --introducer myhome.ddns.net --port 50010 --session demo-room --local-port 50020
artifacts\client\client.exe --introducer myhome.ddns.net --port 50010 --session demo-room --local-port 50021
```

The host and client bind independent local UDP ports. The introducer's hostname must resolve to its current public IP. Configure a No-IP updater on that network/account to refresh the DNS record when the ISP changes the address. DNS only tracks the address; it does not bypass CGNAT or firewalls.

When the client receives the introduced endpoint, both endpoints try LiteNetLib's direct connection. The client sends an application handshake and periodic test message; the host replies. Look for `Direct peer connected`, `Application handshake confirmed`, and `Round trip confirmed`. Type messages in the client after the handshake; `/quit` exits. The client gives up waiting for introduction after 60 seconds; `--introduction-timeout` changes that limit. All applications accept `--timeout` seconds (default 30).

## Local test and two-network test

For a local smoke test, run the introducer first, then host and client with `--introducer 127.0.0.1`, distinct `--local-port` values and the same `--session`. This proves startup, token matching, and application exchange on the same machine, but not public NAT traversal. To test across two internet connections, put the introducer on a publicly reachable VPS or correctly port-forward it at home; run host and client on separate networks. They do not need static inbound forwarding on their own routers, though their NAT/firewall must permit outbound UDP and unsolicited replies to the punched mapping.

Example successful output includes introducer lines `Host registered demo-room` and `Introduced demo-room ...`, server `Peer connected` and `Received ...`, and client `Introduction success`, `Direct peer connected`, and `Round trip confirmed`.

## Troubleshooting and limits

- **DNS failure:** resolve `--introducer` with `nslookup`; check the No-IP updater and record.
- **No introducer registration/request:** verify UDP 50010 on the host firewall, router forwarding/VPS firewall, and the public IP/hostname. ICMP tests do not verify UDP.
- **Client says no live host:** session IDs must match exactly and the host must remain running; the introducer expires host registrations after 120 seconds without refresh.
- **Introduction arrives but no peer connection:** direct punching failed or was blocked. Check peer firewalls, NAT behavior, ISP CGNAT, and endpoint logs. NAT punch-through is not guaranteed; symmetric NAT and restrictive firewall/CGNAT combinations can prevent it. This version has no relay fallback.
- These logs distinguish DNS resolution errors, lack of introducer requests, and unsuccessful direct connection, but UDP reachability can require packet capture or firewall logs to isolate.

The session ID is only a public routing label. There is no authentication, encryption, authorization, or join approval; anyone who guesses the ID can attempt to join. The host caps simultaneous peers at 16 and rejects malformed application messages. Use only for connectivity experiments, not a public service. LiteNetLib uses the fixed `NATTEST/1` protocol discriminator on both ends; it is compiled into the sample, is not secret, and does not authenticate a peer. The application greeting validates protocol compatibility only.
