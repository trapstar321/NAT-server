# LiteNetLib NAT punch-through test

Three Windows console programs exercise LiteNetLib's built-in UDP NAT introduction: an introducer, a session host, and a client. The introducer verifies the compact ES256 host and client tokens issued by MatchmakingService (MM), then exchanges observed endpoints and hands both peers a signed introduction token. It never relays game messages. The host admits a peer only if the peer presents a valid MM client token for the host's session.

## Compatibility and prerequisites

- LiteNetLib **1.3.5** is pinned. NuGet provides its `netstandard2.0` assembly; that is compatible with the shared library and .NET Framework 4.7.2. LiteNetLib 2.x requires .NET Standard 2.1 and is not selected for this Unity Mono target.
- `NatTest.Common` targets .NET Standard 2.0, C# 7.3. The three console programs target .NET Framework 4.7.2.
- **Cryptography uses BouncyCastle (`BouncyCastle.Cryptography` 2.5.1), not `System.Security.Cryptography.ECDsa`.** Unity's Mono class libraries throw `NotImplementedException` from `ECDsa.Create`, so .NET's ECDSA cannot run in a Unity player. The end-to-end tests run the host and client under Unity's Mono runtime to prove the BouncyCastle path works there.
- Install .NET SDK (the repository was built with SDK 9/MSBuild 17.14), NuGet access on first restore, and the .NET Framework 4.7.2 Developer/Targeting Pack to build. Running requires .NET Framework 4.7.2 or later on Windows. The SDK restore supplies `Microsoft.NETFramework.ReferenceAssemblies` where needed.
- For Unity, add LiteNetLib 1.3.5 and the `netstandard2.0` `BouncyCastle.Crypto.dll` to `Assets/Plugins`, then copy `NatTokens.cs` and `SigningKeys.cs` from `NatTest.Common` (or reference the library); keep console entry points out of Unity. See [Porting to Unity](#porting-to-unity).

## Build

From this directory run:

```bat
build.bat
```

Equivalent restore/build commands are `dotnet restore NatTest.sln` and `dotnet build NatTest.sln -c Release`. `build.bat` publishes each net472 application into `artifacts\introducer`, `artifacts\server`, and `artifacts\client` and prints executable paths. Framework-dependent executables use the installed .NET Framework; they are not self-contained.

## Keys

Three key files are involved. Only the introducer's private key is created here:

| Key | Created by | Used by |
|---|---|---|
| MM NAT private key `mm-nat-signing.pem` | MM: `dotnet run -- generate-signing-keys keys` | MM only. Never copy it here. |
| MM NAT public key `mm-nat-signing.pub.pem` | the same MM command | introducer (verifies H and C tokens), host (verifies C tokens) |
| Introducer private key `introducer-signing.pem` | `introducer.exe generate-key keys` | introducer only |
| Introducer public key `introducer-signing.pub.pem` | the same introducer command | host and client (verify introduction tokens) |

```bat
artifacts\introducer\introducer.exe generate-key keys
```

`generate-key` refuses to overwrite existing files, because regenerating the key invalidates outstanding introduction tokens and requires redistributing the public key. Keep the private PEM on the introducer machine only: restrict its file ACLs and never commit it. `keys/` and `*.pem` are git-ignored. By default every program looks for its keys under `keys\` in its working directory; override with the options below.

## Start a session

Make UDP 50010 reachable on the introducer machine. On a public VPS, allow inbound UDP 50010 in its network firewall and Windows Firewall. At home, forward UDP 50010 on the router to the introducer PC and allow it through Windows Firewall. Binding to `0.0.0.0` only listens on local interfaces; it does not make the service internet-reachable.

Create a hosted game (`POST /games`) and join it (`POST /join`) through MM, then pass the returned host and client tokens to the corresponding programs. Pass the listing's `hostPlayerId` to the client so it can check it is introduced to the right host.

Run these in separate consoles (change the address and placeholders):

```bat
artifacts\introducer\introducer.exe --bind 0.0.0.0 --port 50010 --mm-public-key keys\mm-nat-signing.pub.pem --key keys\introducer-signing.pem
artifacts\server\server.exe --introducer myhome.ddns.net --port 50010 --token HOST_TOKEN --local-port 50020 --mm-public-key keys\mm-nat-signing.pub.pem --introducer-public-key keys\introducer-signing.pub.pem
artifacts\client\client.exe --introducer myhome.ddns.net --port 50010 --token CLIENT_TOKEN --host-player HOST_PLAYER_ID --local-port 50021 --introducer-public-key keys\introducer-signing.pub.pem
```

| Program | Option | Default | Notes |
|---|---|---|---|
| introducer | `--timeout` | 30 | seconds a host registration stays live without a new registration |
| introducer | `--introduction-lifetime` | 60 | seconds an introduction token is valid (5–600) |
| server | `--timeout` | 30 | LiteNetLib disconnect timeout |
| client | `--introduce-timeout` / `--connect-timeout` | 5 / 5 | seconds to wait for an introduction / the direct connection |
| client | `--host-player` | none | expected host player ID; strongly recommended |

The host and client bind independent local UDP ports. The introducer's hostname must resolve to its current public IP. Configure a No-IP updater on that network/account to refresh the DNS record when the ISP changes the address. DNS only tracks the address; it does not bypass CGNAT or firewalls.

The introducer acknowledges successful host registrations and valid client requests over unconnected UDP. It returns `NATTEST/1|INTRODUCER_ERR|<reason>` for rejected requests. The host retries registration until it receives an acknowledgement or error, then stops sending requests. An acknowledgement confirms the request reached the introducer; it does not confirm that a matching host exists or that the peer connection will succeed. After connecting, the client sends one `hello` message; the server logs it with the player ID.

## Authorization flow

1. **Host registration.** The host sends its MM host token (role `H`). The introducer verifies MM's signature and expiry with MM's public key and registers the token's session for the token's player. Since LiteNetLib uses one request type for registering and for asking to be introduced, the signed role selects the operation; a `C` token can never register a host and vice versa. A later registration for the same session is rejected if it carries a different player ID (`host-player-mismatch`), or comes from a different endpoint while the current registration is live (`duplicate-host-registration`). Registrations are dropped when they go stale (`--timeout`) or the host token expires.
2. **Introduction request.** The client sends its MM client token (role `C`). The introducer verifies it and looks up the token's session. It does not need, and never sees, the private-game secret: MM checked it before issuing the client token.
3. **Introduction.** The introducer calls `NatIntroduce(..., introductionToken)`. LiteNetLib delivers that string to both peers, and each peer relays it to the other in its punch packets, so the token in `OnNatIntroductionSuccess` came from the *other peer's* punch. Anyone can send a punch, which is why the token is signed.
4. **Client check.** In `OnNatIntroductionSuccess` the client verifies the introduction token with the introducer's public key and checks that the session and client player ID are its own (read from its client token) and that the host player ID matches `--host-player`. Only then does it connect. A failed check is logged as `Ignored introduction ... : <reason>`.
5. **Host check.** The client's connection request carries `NATTEST/1` and then its MM client token. In `OnConnectionRequest` the host verifies the token with MM's public key and requires role `C`, its own session, and no other open connection for that player. A rejected request carries the reason back to the client. The host also verifies introduction tokens it receives and logs which player was introduced, but the MM client token is what authorizes the connection.

The host token never leaves the host, and no token carries a password, email, private-game secret, or owner secret.

### Introduction token format

Fixed-width binary, unpadded Base64URL: 116 bytes, **155 characters** (LiteNetLib allows 256).

| Offset | Size | Field |
|---|---|---|
| 0 | 16 | Session ID, RFC 4122 big-endian GUID bytes |
| 16 | 16 | Host player ID, same encoding |
| 32 | 16 | Client player ID, same encoding |
| 48 | 4 | Expiration: unsigned 32-bit big-endian Unix seconds |
| 52 | 64 | ES256 signature over bytes 0–51, raw IEEE P1363 `R \|\| S` |

GUIDs, expiration, signature and Base64URL rules are the same as the MM host/client token (see the MatchmakingService README). Decoders accept only the canonical encoding of exactly the expected length. The introducer signs with deterministic RFC 6979 nonces. The two formats differ in length and are signed with different keys, so neither is accepted as the other.

Rejection reasons:

| Where | Reason | Meaning |
|---|---|---|
| introducer | `invalid-token` | malformed, wrong length, bad role, or bad MM signature |
| introducer | `token-expired` | MM token past its expiration |
| introducer | `host-player-mismatch`, `duplicate-host-registration`, `session-capacity-reached` | host registration refused |
| introducer | `no-live-host`, `self-introduction` | client request refused |
| client | `invalid-token`, `token-expired`, `session-mismatch`, `client-player-mismatch`, `host-player-mismatch` | introduction ignored, no connection attempted |
| host | `malformed-request`, `protocol-mismatch`, `invalid-token`, `token-expired`, `client-token-required`, `session-mismatch`, `player-already-connected`, `endpoint-in-use`, `peer-limit-reached` | connection rejected |

## Tests

```bat
dotnet test tests\NatTest.Tests\NatTest.Tests.csproj
```

- **Unit tests** cover both token formats: layout, GUID byte order, expiry, roles, every-byte tampering, wrong keys, non-canonical encodings, format confusion and size. They also cover key loading and rejection.
- **End-to-end tests** build and start the real MatchmakingService from the sibling `..\MatchmakingService` directory (override with `NATTEST_MATCHMAKING_PROJECT`). They use throwaway keys, a temporary database and raised rate limits. Keys are generated with MM's and the introducer's own commands. Then they start `introducer.exe` and register (or log in) a host and two client accounts. They run on loopback, so they test the protocol and authorization, not NAT traversal. They cover:
  - public and private games connecting from `POST /games` and `POST /join` through the introducer to a `hello` on the host;
  - MM-issued tokens verifying in `NatTest.Common` with the identities from the API;
  - the introducer rejecting tampered, foreign-key, expired, no-host and hijacking requests;
  - the host rejecting every invalid or misused connection token and duplicate players;
  - the client refusing introductions forged by a fake introducer: wrong key, expired, wrong session, wrong client, wrong host, or a relayed MM token. A control case with a correctly signed token through the same fake introducer does connect.
- **Unity Mono:** the host and client tests also run `server.exe` and `client.exe` under the newest Unity Hub editor's Mono runtime with its `unityjit-win32` class libraries (override with `NATTEST_UNITY_MONO=<path to mono.exe>`). They are skipped if no editor is found.

## Porting to Unity

- Unity standalone players on the Mono backend run the same class libraries the tests use. **IL2CPP was not tested.** If you switch to IL2CPP with managed code stripping, preserve BouncyCastle with a `link.xml` and test a player build.
- The console programs load NuGet's `net461` BouncyCastle assembly; Unity would use the `netstandard2.0` one. They are built from the same source, but test a Unity player build anyway.
- The Unity `GameServer.OnConnectionRequest` currently accepts the key `"public"` or the private-game secret. Replace that with the MM client-token check from `HostedGameServer.AuthorizeConnection`, so that only players MM authorized can connect.
- The Unity `Client.OnNatIntroductionSuccess` connects immediately. Add the `CheckIntroduction` gate from `HostedGameClient` first, and send the protocol key plus the client token as connection data instead of the connection key.
- Hosts and clients need the public keys (MM NAT and introducer), never the private keys.

## Local test and two-network test

The end-to-end tests are the local smoke test. To test across two internet connections, put the introducer on a publicly reachable VPS or correctly port-forward it at home; run host and client on separate networks. They do not need static inbound forwarding on their own routers, though their NAT/firewall must permit outbound UDP and unsolicited replies to the punched mapping.

Example successful output includes:

- **Introducer:** `Host registered <session> player <host>` and `NatIntroduce called`.
- **Server:** `Verified introduction of player <client>`, `Accepted player <client>`, and `Received from player <client> ...: hello`.
- **Client:** `Introduction success` and `Direct peer connected`.

## Troubleshooting and limits

- **DNS failure:** resolve `--introducer` with `nslookup`; check the No-IP updater and record.
- **No introducer registration/request:** verify UDP 50010 on the host firewall, router forwarding/VPS firewall, and the public IP/hostname. ICMP tests do not verify UDP.
- **`invalid-token` everywhere:** the introducer, host and MM must agree on MM's NAT key pair; check that `--mm-public-key` is the `.pub.pem` from the MM instance that issued the tokens. Client `Ignored introduction ... invalid-token` means the client's `--introducer-public-key` does not match the introducer's `--key`.
- **`token-expired` on the client token:** MM client tokens last 2 minutes; call `POST /join` again. Check the clocks on all machines; expiry has no clock-skew allowance.
- **Client says no live host:** the host must be registered with the introducer, its registration must not be stale, and its host token must not be expired.
- **Introduction arrives but no peer connection:** direct punching failed or was blocked. Check peer firewalls, NAT behavior, ISP CGNAT, and endpoint logs. NAT punch-through is not guaranteed; symmetric NAT and restrictive firewall/CGNAT combinations can prevent it. This version has no relay fallback.
- These logs distinguish DNS resolution errors, lack of introducer requests, and unsuccessful direct connection, but UDP reachability can require packet capture or firewall logs to isolate.

Tokens are bearer credentials: anyone who captures one on the network can use it until it expires, and traffic is not encrypted. The short client-token and introduction-token lifetimes limit replay, and the host allows one connection per player. The session ID is a public routing label. The host caps simultaneous peers at 16 and rejects malformed application messages. `NATTEST/1` is a fixed protocol discriminator, not a secret.
