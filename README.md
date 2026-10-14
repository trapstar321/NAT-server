# LiteNetLib NAT punch-through test

Three Windows console programs exercise LiteNetLib's built-in UDP NAT introduction: an introducer, a session host, and a client. The introducer verifies MatchmakingService HMAC-SHA-256 tokens, then exchanges observed endpoints without relaying game messages.

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

Configure the introducer with the same signing key as MatchmakingService, using the `MATCHMAKING__TOKENSIGNINGKEY` environment variable or the `--token-key` argument. Keep the key private. Create a hosted game and join it through MatchmakingService, then pass the returned host and client tokens to the corresponding programs.

Run these in separate consoles (change the address and token placeholders):

```bat
artifacts\introducer\introducer.exe --bind 0.0.0.0 --port 50010 --token-key YOUR_MATCHMAKING_SIGNING_KEY
artifacts\server\server.exe --introducer myhome.ddns.net --port 50010 --token HOST_TOKEN --local-port 50020
artifacts\client\client.exe --introducer myhome.ddns.net --port 50010 --token CLIENT_TOKEN --local-port 50021 --connect-timeout 5 --introduce-timeout 5
```

The host and client bind independent local UDP ports. The introducer's hostname must resolve to its current public IP. Configure a No-IP updater on that network/account to refresh the DNS record when the ISP changes the address. DNS only tracks the address; it does not bypass CGNAT or firewalls.

The introducer acknowledges successful host registrations and valid client requests over unconnected UDP. It returns `NATTEST/1|INTRODUCER_ERR|<reason>` for invalid tokens, duplicate host registrations, capacity limits, missing hosts, and self-introductions. The host retries registration until it receives an acknowledgement or error, then stops sending requests. Host (`H`) tokens do not expire, and host registrations remain available until replaced or the introducer stops. Client (`C`) tokens must be unexpired. Server and Client print acknowledgements and Introducer errors. An acknowledgement confirms the request reached the introducer; it does not confirm that a matching host exists or that the peer connection will succeed. When the client receives the introduced endpoint, it starts the LiteNetLib direct connection. After connecting, the client sends one `hello` message; the server logs the received message. The Client's `--introduce-timeout` controls how long it waits for NAT introduction, and `--connect-timeout` controls how long it waits for the direct peer connection. Both default to 5 seconds.

## Local test and two-network test

For a local smoke test, use valid host and client tokens issued by MatchmakingService, run the introducer first, then host and client with `--introducer 127.0.0.1` and distinct `--local-port` values. This proves startup, token matching, and application exchange on the same machine, but not public NAT traversal. To test across two internet connections, put the introducer on a publicly reachable VPS or correctly port-forward it at home; run host and client on separate networks. They do not need static inbound forwarding on their own routers, though their NAT/firewall must permit outbound UDP and unsolicited replies to the punched mapping.

Example successful output includes introducer lines `Host registered <session>` and `Introduced <session> ...`, server `Peer connected` and `Received ...: hello`, and client `Introduction success` and `Direct peer connected`.

## Troubleshooting and limits

- **DNS failure:** resolve `--introducer` with `nslookup`; check the No-IP updater and record.
- **No introducer registration/request:** verify UDP 50010 on the host firewall, router forwarding/VPS firewall, and the public IP/hostname. ICMP tests do not verify UDP.
- **Client says no live host:** session IDs must match exactly and the host must be registered with the introducer.
- **Introduction arrives but no peer connection:** direct punching failed or was blocked. Check peer firewalls, NAT behavior, ISP CGNAT, and endpoint logs. NAT punch-through is not guaranteed; symmetric NAT and restrictive firewall/CGNAT combinations can prevent it. This version has no relay fallback.
- These logs distinguish DNS resolution errors, lack of introducer requests, and unsuccessful direct connection, but UDP reachability can require packet capture or firewall logs to isolate.

The session ID is only a public routing label. There is no authentication, encryption, authorization, or join approval; anyone who guesses the ID can attempt to join. The host caps simultaneous peers at 16 and rejects malformed application messages. Use only for connectivity experiments, not a public service. LiteNetLib uses the fixed `NATTEST/1` protocol discriminator on both ends; it is compiled into the sample, is not secret, and does not authenticate a peer. The application greeting validates protocol compatibility only.
