using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using NatTest.Common;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace NatTest.Tests.EndToEnd
{
    // Runs the real MatchmakingService, introducer, host (server.exe) and client (client.exe) on loopback. Host and client
    // tests that matter for the Unity port run both on .NET Framework and on Unity's Mono runtime.
    [Collection(EndToEndCollection.Name)]
    public sealed class EndToEndTests
    {
        private readonly EndToEndFixture fixture;

        public EndToEndTests(EndToEndFixture fixture)
        {
            this.fixture = fixture;
        }

        [Fact]
        public Task Public_game_host_and_client_connect_through_the_introducer() { return PublicGameConnects(PeerRuntime.NetFramework); }

        [UnityMonoFact]
        public Task Public_game_host_and_client_connect_through_the_introducer_on_unity_mono() { return PublicGameConnects(PeerRuntime.UnityMono); }

        [Fact]
        public async Task Private_game_secret_is_checked_by_MM_and_the_resulting_token_connects()
        {
            using (var host = await StartHost(PeerRuntime.NetFramework, "private", "open sesame"))
            {
                foreach (string wrongSecret in new[] { null, "", "open sesame!" })
                    using (var response = await fixture.Api.JoinRaw(fixture.Client, host.Game.SessionId, wrongSecret))
                        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

                string clientToken = await fixture.Api.Join(fixture.Client, host.Game.SessionId, "open sesame");
                using (var client = StartClient(PeerRuntime.NetFramework, clientToken, host.Game.HostPlayerId))
                {
                    await client.WaitForLine("Direct peer connected");
                    await host.Process.WaitForLine("Accepted player " + fixture.Client.PlayerId);
                }
            }
        }

        [Fact]
        public async Task Real_MM_tokens_verify_with_NatTest_Common_and_carry_the_API_identities()
        {
            var created = await fixture.Api.CreateGame(fixture.Host, "public", null);
            string clientToken = await fixture.Api.Join(fixture.Client, created.SessionId);
            var mmKey = SigningKeys.LoadPublicKeyFile(fixture.MatchmakingPublicKeyPath, "MM NAT key");
            long now = NatTokens.NowUnixSeconds();

            MatchmakingTokenClaims hostClaims, clientClaims;
            string error;
            Assert.True(NatTokens.TryVerifyMatchmakingToken(created.Token, mmKey, now, out hostClaims, out error), error);
            Assert.True(NatTokens.TryVerifyMatchmakingToken(clientToken, mmKey, now, out clientClaims, out error), error);
            Assert.Equal(135, created.Token.Length);
            Assert.Equal(135, clientToken.Length);

            Assert.Equal('H', hostClaims.Role);
            Assert.Equal(created.SessionId, hostClaims.SessionId);
            Assert.Equal(fixture.Host.PlayerId, hostClaims.PlayerId);
            Assert.Equal(created.HostPlayerId, hostClaims.PlayerId);
            Assert.InRange(hostClaims.ExpiresUnixSeconds - now, 24 * 3600 - 30, 24 * 3600 + 30);

            Assert.Equal('C', clientClaims.Role);
            Assert.Equal(created.SessionId, clientClaims.SessionId);
            Assert.Equal(fixture.Client.PlayerId, clientClaims.PlayerId);
            Assert.InRange(clientClaims.ExpiresUnixSeconds - now, 120 - 30, 120 + 30);

            // The MM key pair written by MM's own generate-signing-keys loads here too, and its signatures verify.
            var data = new byte[] { 1, 2, 3 };
            Assert.True(SigningKeys.Verify(mmKey, data, 0, 3, SigningKeys.Sign(fixture.MatchmakingPrivateKey, data, 0, 3), 0));
        }

        [Fact]
        public async Task Introducer_rejects_client_tokens_that_are_tampered_foreign_or_expired()
        {
            using (var host = await StartHost(PeerRuntime.NetFramework))
            {
                Guid session = host.Game.SessionId;
                Guid player = fixture.Client.PlayerId;
                long now = NatTokens.NowUnixSeconds();
                var foreignKey = (ECPrivateKeyParameters)SigningKeys.Generate().Private;
                string valid = await fixture.Api.Join(fixture.Client, session);

                var cases = new Dictionary<string, string>
                {
                    { TestTokens.FlipBit(valid, 70), NatTokens.InvalidToken },
                    { TestTokens.FlipBit(valid, 0), NatTokens.InvalidToken },
                    { TestTokens.Matchmaking(foreignKey, session, player, 'C', now + 120), NatTokens.InvalidToken },
                    { TestTokens.Matchmaking(fixture.MatchmakingPrivateKey, session, player, 'C', now - 1), NatTokens.ExpiredToken }
                };
                foreach (var testCase in cases)
                    await AssertIntroducerRejectsClient(testCase.Key, host.Game.HostPlayerId, testCase.Value);

                Assert.False(host.Process.HasLine("Accepted player"), host.Process.Output);
            }
        }

        [Fact]
        public async Task Introducer_reports_no_live_host_for_a_session_whose_host_never_registered()
        {
            var created = await fixture.Api.CreateGame(fixture.Host, "public", null);
            await AssertIntroducerRejectsClient(await fixture.Api.Join(fixture.Client, created.SessionId), created.HostPlayerId, "no-live-host");
        }

        [Fact]
        public async Task Host_registrations_cannot_be_taken_over()
        {
            using (var host = await StartHost(PeerRuntime.NetFramework))
            {
                await fixture.Introducer.WaitForLine("Host registered " + host.Game.SessionId + " player " + fixture.Host.PlayerId);
                string otherPlayersHostToken = TestTokens.Matchmaking(fixture.MatchmakingPrivateKey, host.Game.SessionId, fixture.OtherClient.PlayerId, 'H', NatTokens.NowUnixSeconds() + 3600);

                using (var attacker = new LitePeer())
                {
                    Assert.Equal("NATTEST/1|INTRODUCER_ERR|duplicate-host-registration", await attacker.SendIntroduceRequest(fixture.IntroducerPort, host.Game.Token));
                }
                using (var attacker = new LitePeer())
                {
                    Assert.Equal("NATTEST/1|INTRODUCER_ERR|host-player-mismatch", await attacker.SendIntroduceRequest(fixture.IntroducerPort, otherPlayersHostToken));
                }
            }
        }

        [Fact]
        public async Task MM_owner_tokens_are_refused_by_the_introducer_and_the_host()
        {
            using (var host = await StartHost(PeerRuntime.NetFramework))
            {
                string ownerToken = await fixture.Api.OwnerToken(fixture.Client);
                MatchmakingTokenClaims claims;
                string error;
                Assert.True(NatTokens.TryVerifyMatchmakingToken(ownerToken, SigningKeys.LoadPublicKeyFile(fixture.MatchmakingPublicKeyPath, "MM NAT key"),
                    NatTokens.NowUnixSeconds(), out claims, out error), error);
                Assert.Equal('O', claims.Role);
                Assert.Equal(Guid.Empty, claims.SessionId);
                Assert.Equal(fixture.Client.PlayerId, claims.PlayerId);

                using (var peer = new LitePeer())
                    Assert.Equal("NATTEST/1|INTRODUCER_ERR|" + NatTokens.InvalidToken, await peer.SendIntroduceRequest(fixture.IntroducerPort, ownerToken));
                await AssertHostRejects(host, "NATTEST/1", ownerToken, "client-token-required");
            }
        }

        [Fact]
        public async Task Client_program_refuses_a_host_token()
        {
            var created = await fixture.Api.CreateGame(fixture.Host, "public", null);
            using (var client = StartClient(PeerRuntime.NetFramework, created.Token, created.HostPlayerId))
            {
                await client.WaitForLine("--token must be a client (C) token");
                await client.WaitForExit(PeerProcess.DefaultTimeout);
            }
        }

        [Fact]
        public Task Host_accepts_only_valid_client_tokens_for_its_session() { return HostAuthorizesConnections(PeerRuntime.NetFramework); }

        [UnityMonoFact]
        public Task Host_accepts_only_valid_client_tokens_for_its_session_on_unity_mono() { return HostAuthorizesConnections(PeerRuntime.UnityMono); }

        [Fact]
        public Task Client_connects_only_for_introductions_signed_by_the_introducer_for_its_pairing() { return ClientChecksIntroductions(PeerRuntime.NetFramework); }

        [UnityMonoFact]
        public Task Client_connects_only_for_introductions_signed_by_the_introducer_for_its_pairing_on_unity_mono() { return ClientChecksIntroductions(PeerRuntime.UnityMono); }

        private async Task PublicGameConnects(PeerRuntime runtime)
        {
            using (var host = await StartHost(runtime))
            {
                await fixture.Introducer.WaitForLine("Host registered " + host.Game.SessionId + " player " + fixture.Host.PlayerId);
                string clientToken = await fixture.Api.Join(fixture.Client, host.Game.SessionId);
                Guid clientId = fixture.Client.PlayerId;

                using (var client = StartClient(runtime, clientToken, host.Game.HostPlayerId))
                {
                    await client.WaitForLine("Introduction success");
                    await client.WaitForLine("Direct peer connected");
                    await host.Process.WaitForLine("Verified introduction of player " + clientId);
                    await host.Process.WaitForLine("Accepted player " + clientId);
                    string received = await host.Process.WaitForLine("Received from player " + clientId);
                    Assert.EndsWith(": hello", received);
                    Assert.False(client.HasLine("Ignored introduction"), client.Output);
                    Assert.False(host.Process.HasLine("Ignored introduction"), host.Process.Output);
                }
            }
        }

        private async Task HostAuthorizesConnections(PeerRuntime runtime)
        {
            using (var host = await StartHost(runtime))
            {
                Guid session = host.Game.SessionId;
                long now = NatTokens.NowUnixSeconds();
                string clientToken = await fixture.Api.Join(fixture.Client, session);
                var otherGame = await fixture.Api.CreateGame(fixture.Host, "public", null);
                string otherSessionToken = await fixture.Api.Join(fixture.Client, otherGame.SessionId);
                var foreignKey = (ECPrivateKeyParameters)SigningKeys.Generate().Private;

                await AssertHostRejects(host, "WRONG/1", clientToken, "protocol-mismatch");
                await AssertHostRejects(host, null, null, "malformed-request");
                await AssertHostRejects(host, "NATTEST/1", "not-a-token", NatTokens.InvalidToken);
                await AssertHostRejects(host, "NATTEST/1", TestTokens.FlipBit(clientToken, 80), NatTokens.InvalidToken);
                await AssertHostRejects(host, "NATTEST/1", TestTokens.Matchmaking(foreignKey, session, fixture.Client.PlayerId, 'C', now + 120), NatTokens.InvalidToken);
                await AssertHostRejects(host, "NATTEST/1", TestTokens.Matchmaking(fixture.MatchmakingPrivateKey, session, fixture.Client.PlayerId, 'C', now - 1), NatTokens.ExpiredToken);
                await AssertHostRejects(host, "NATTEST/1", host.Game.Token, "client-token-required");
                await AssertHostRejects(host, "NATTEST/1", otherSessionToken, "session-mismatch");
                Assert.False(host.Process.HasLine("Accepted player"), host.Process.Output);

                using (var first = new LitePeer())
                using (var duplicate = new LitePeer())
                using (var second = new LitePeer())
                {
                    Assert.Null(await first.Connect(host.Port, "NATTEST/1", clientToken));
                    await host.Process.WaitForLine("Accepted player " + fixture.Client.PlayerId);
                    Assert.Equal("player-already-connected", await duplicate.Connect(host.Port, "NATTEST/1", clientToken));
                    Assert.Null(await second.Connect(host.Port, "NATTEST/1", await fixture.Api.Join(fixture.OtherClient, session)));
                    await host.Process.WaitForLine("Accepted player " + fixture.OtherClient.PlayerId);
                }
            }
        }

        private async Task ClientChecksIntroductions(PeerRuntime runtime)
        {
            using (var host = await StartHost(runtime))
            using (var rogue = new RogueIntroducer(host.Port))
            {
                Guid session = host.Game.SessionId;
                Guid hostId = host.Game.HostPlayerId;
                var foreignKey = (ECPrivateKeyParameters)SigningKeys.Generate().Private;
                var introducerKey = fixture.IntroducerPrivateKey;
                Func<string, Guid> clientOf = token =>
                {
                    MatchmakingTokenClaims claims;
                    Assert.True(NatTokens.TryReadMatchmakingToken(token, out claims));
                    return claims.PlayerId;
                };

                var forgeries = new Dictionary<string, Func<string, string>>
                {
                    { NatTokens.InvalidToken, token => NatTokens.CreateIntroductionToken(foreignKey, session, hostId, clientOf(token), NatTokens.NowUnixSeconds() + 60) },
                    { NatTokens.ExpiredToken, token => NatTokens.CreateIntroductionToken(introducerKey, session, hostId, clientOf(token), NatTokens.NowUnixSeconds() - 1) },
                    { "session-mismatch", token => NatTokens.CreateIntroductionToken(introducerKey, Guid.NewGuid(), hostId, clientOf(token), NatTokens.NowUnixSeconds() + 60) },
                    { "client-player-mismatch", token => NatTokens.CreateIntroductionToken(introducerKey, session, hostId, fixture.OtherClient.PlayerId, NatTokens.NowUnixSeconds() + 60) },
                    { "host-player-mismatch", token => NatTokens.CreateIntroductionToken(introducerKey, session, fixture.OtherClient.PlayerId, clientOf(token), NatTokens.NowUnixSeconds() + 60) }
                };

                foreach (var forgery in forgeries)
                {
                    rogue.IntroductionToken = forgery.Value;
                    using (var client = StartClient(runtime, await fixture.Api.Join(fixture.Client, session), hostId, rogue.Port))
                    {
                        await client.WaitForLine("): " + forgery.Key + ".");
                        await client.WaitForExit(PeerProcess.DefaultTimeout);
                        Assert.False(client.HasLine("Direct peer connected"), client.Output);
                    }
                }

                // Relaying an MM token in place of an introduction token is rejected too.
                rogue.IntroductionToken = token => token;
                using (var client = StartClient(runtime, await fixture.Api.Join(fixture.Client, session), hostId, rogue.Port))
                {
                    await client.WaitForLine("): " + NatTokens.InvalidToken + ".");
                    await client.WaitForExit(PeerProcess.DefaultTimeout);
                    Assert.False(client.HasLine("Direct peer connected"), client.Output);
                }
                Assert.False(host.Process.HasLine("Accepted player"), host.Process.Output);

                // Control: the same fake introducer with a correctly signed token does lead to a connection, so the
                // rejections above are caused by the token checks and not by the test setup.
                rogue.IntroductionToken = token => NatTokens.CreateIntroductionToken(introducerKey, session, hostId, clientOf(token), NatTokens.NowUnixSeconds() + 60);
                using (var client = StartClient(runtime, await fixture.Api.Join(fixture.Client, session), hostId, rogue.Port))
                {
                    await client.WaitForLine("Direct peer connected");
                    await host.Process.WaitForLine("Accepted player " + fixture.Client.PlayerId);
                }
            }
        }

        private async Task AssertIntroducerRejectsClient(string token, Guid hostPlayerId, string reason)
        {
            using (var client = StartClient(PeerRuntime.NetFramework, token, hostPlayerId))
            {
                await client.WaitForLine("Introducer rejected the client request: " + reason + ".");
                await client.WaitForExit(PeerProcess.DefaultTimeout);
                Assert.False(client.HasLine("Direct peer connected"), client.Output);
            }
        }

        private static async Task AssertHostRejects(HostedGame host, string protocol, string token, string reason)
        {
            using (var peer = new LitePeer())
                Assert.Equal(reason, await peer.Connect(host.Port, protocol, token));
            await host.Process.WaitForLine(": " + reason + ".");
        }

        private async Task<HostedGame> StartHost(PeerRuntime runtime, string gameType = "public", string secret = null)
        {
            var game = await fixture.Api.CreateGame(fixture.Host, gameType, secret);
            int port = TestEnvironment.FreeUdpPort();
            var process = PeerProcess.Start("host", TestEnvironment.ServerExe, new object[]
            {
                "--introducer", "127.0.0.1", "--port", fixture.IntroducerPort, "--token", game.Token, "--local-port", port,
                "--mm-public-key", fixture.MatchmakingPublicKeyPath, "--introducer-public-key", fixture.IntroducerPublicKeyPath
            }, runtime);
            try
            {
                await process.WaitForLine("Introducer acknowledged the host registration.");
            }
            catch
            {
                process.Dispose();
                throw;
            }
            return new HostedGame(game, process, port);
        }

        private PeerProcess StartClient(PeerRuntime runtime, string token, Guid hostPlayerId, int? introducerPort = null)
        {
            return PeerProcess.Start("client", TestEnvironment.ClientExe, new object[]
            {
                "--introducer", "127.0.0.1", "--port", introducerPort ?? fixture.IntroducerPort, "--token", token,
                "--local-port", TestEnvironment.FreeUdpPort(), "--host-player", hostPlayerId,
                "--introducer-public-key", fixture.IntroducerPublicKeyPath, "--introduce-timeout", "4", "--connect-timeout", "5"
            }, runtime);
        }

        private sealed class HostedGame : IDisposable
        {
            internal HostedGame(CreatedGame game, PeerProcess process, int port)
            {
                Game = game;
                Process = process;
                Port = port;
            }

            internal CreatedGame Game { get; private set; }
            internal PeerProcess Process { get; private set; }
            internal int Port { get; private set; }

            public void Dispose() { Process.Dispose(); }
        }
    }
}
