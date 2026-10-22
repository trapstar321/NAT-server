using System;
using NatTest.Common;
using Org.BouncyCastle.Crypto.Parameters;
using Xunit;

namespace NatTest.Tests
{
    public sealed class NatTokensTests
    {
        private static readonly Guid Session = new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff");
        private static readonly Guid Host = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly Guid Client = new Guid("7c9e6679-7425-40de-944b-e07fc1f90ae7");

        private readonly ECPrivateKeyParameters mmKey = (ECPrivateKeyParameters)SigningKeys.Generate().Private;
        private readonly ECPrivateKeyParameters introducerKey = (ECPrivateKeyParameters)SigningKeys.Generate().Private;
        private readonly long now = NatTokens.NowUnixSeconds();

        [Fact]
        public void Guids_use_rfc4122_big_endian_byte_order_and_round_trip()
        {
            var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
            var bytes = new byte[16];
            NatTokens.WriteGuid(guid, bytes, 0);
            Assert.Equal(new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff }, bytes);
            Assert.Equal(guid, NatTokens.ReadGuid(bytes, 0));
        }

        [Fact]
        public void Matchmaking_token_verifies_and_exposes_its_claims()
        {
            string token = TestTokens.Matchmaking(mmKey, Session, Client, 'C', now + 120);
            Assert.Equal(135, token.Length);

            MatchmakingTokenClaims claims;
            string error;
            Assert.True(NatTokens.TryVerifyMatchmakingToken(token, PublicOf(mmKey), now, out claims, out error));
            Assert.Null(error);
            Assert.Equal(Session, claims.SessionId);
            Assert.Equal(Client, claims.PlayerId);
            Assert.Equal('C', claims.Role);
            Assert.Equal(now + 120, claims.ExpiresUnixSeconds);
        }

        [Fact]
        public void Changing_any_byte_of_a_matchmaking_token_invalidates_it()
        {
            string token = TestTokens.Matchmaking(mmKey, Session, Host, 'H', now + 120);
            for (int i = 0; i < NatTokens.MatchmakingTokenLength; i++)
                AssertMatchmakingRejected(TestTokens.FlipBit(token, i), NatTokens.InvalidToken);
        }

        [Fact]
        public void Host_and_client_roles_cannot_be_swapped()
        {
            byte[] bytes = TestTokens.Decode(TestTokens.Matchmaking(mmKey, Session, Host, 'H', now + 120));
            bytes[32] = (byte)'C';
            AssertMatchmakingRejected(NatTokens.Base64UrlEncode(bytes), NatTokens.InvalidToken);
        }

        [Fact]
        public void Matchmaking_tokens_from_another_key_are_rejected()
        {
            AssertMatchmakingRejected(TestTokens.Matchmaking(introducerKey, Session, Client, 'C', now + 120), NatTokens.InvalidToken);
        }

        [Fact]
        public void Expired_matchmaking_tokens_are_rejected()
        {
            AssertMatchmakingRejected(TestTokens.Matchmaking(mmKey, Session, Client, 'C', now), NatTokens.ExpiredToken);
            MatchmakingTokenClaims claims;
            string error;
            Assert.True(NatTokens.TryVerifyMatchmakingToken(TestTokens.Matchmaking(mmKey, Session, Client, 'C', now + 1), PublicOf(mmKey), now, out claims, out error));
        }

        [Theory]
        [InlineData('X')]
        [InlineData('h')]
        [InlineData('\0')]
        public void Unknown_roles_are_rejected_even_when_signed(char role)
        {
            AssertMatchmakingRejected(TestTokens.Matchmaking(mmKey, Session, Client, role, now + 120), NatTokens.InvalidToken);
        }

        [Fact]
        public void Empty_ids_and_zero_expiration_are_rejected_even_when_signed()
        {
            AssertMatchmakingRejected(TestTokens.Matchmaking(mmKey, Guid.Empty, Client, 'C', now + 120), NatTokens.InvalidToken);
            AssertMatchmakingRejected(TestTokens.Matchmaking(mmKey, Session, Guid.Empty, 'C', now + 120), NatTokens.InvalidToken);
            AssertMatchmakingRejected(TestTokens.Matchmaking(mmKey, Session, Client, 'C', 0), NatTokens.InvalidToken);
        }

        [Fact]
        public void Owner_tokens_verify_only_with_an_empty_session()
        {
            MatchmakingTokenClaims claims;
            string error;
            Assert.True(NatTokens.TryVerifyMatchmakingToken(TestTokens.Matchmaking(mmKey, Guid.Empty, Client, 'O', now + 120), PublicOf(mmKey), now, out claims, out error));
            Assert.Equal('O', claims.Role);
            Assert.Equal(Guid.Empty, claims.SessionId);
            Assert.Equal(Client, claims.PlayerId);
            AssertMatchmakingRejected(TestTokens.Matchmaking(mmKey, Session, Client, 'O', now + 120), NatTokens.InvalidToken);
        }

        [Fact]
        public void Non_canonical_or_malformed_encodings_are_rejected()
        {
            string token = TestTokens.Matchmaking(mmKey, Session, Client, 'C', now + 120);
            string standardBase64 = Convert.ToBase64String(TestTokens.Decode(token));
            // 101 bytes leave 2 unused bits in the last character; setting one decodes to the same bytes but is non-canonical.
            char last = token[token.Length - 1];
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            string nonZeroTrailingBits = token.Substring(0, token.Length - 1) + alphabet[alphabet.IndexOf(last) | 1];

            AssertMatchmakingRejected(token + "=", NatTokens.InvalidToken);
            AssertMatchmakingRejected(standardBase64, NatTokens.InvalidToken);
            AssertMatchmakingRejected(" " + token, NatTokens.InvalidToken);
            AssertMatchmakingRejected(token.Substring(1), NatTokens.InvalidToken);
            AssertMatchmakingRejected(token + "A", NatTokens.InvalidToken);
            AssertMatchmakingRejected("", NatTokens.InvalidToken);
            AssertMatchmakingRejected(null, NatTokens.InvalidToken);
            if (nonZeroTrailingBits != token) AssertMatchmakingRejected(nonZeroTrailingBits, NatTokens.InvalidToken);
        }

        [Fact]
        public void Introduction_token_is_116_bytes_with_a_raw_signature_and_fits_LiteNetLib()
        {
            string token = NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Client, now + 60);
            Assert.Equal(155, token.Length);
            Assert.True(token.Length <= NatTokens.LiteNetLibMaxTokenLength);
            Assert.Equal(NatTokens.IntroductionTokenLength, TestTokens.Decode(token).Length);
            Assert.Equal(116, NatTokens.IntroductionPayloadLength + SigningKeys.SignatureLength);

            IntroductionTokenClaims claims;
            string error;
            Assert.True(NatTokens.TryVerifyIntroductionToken(token, PublicOf(introducerKey), now, out claims, out error));
            Assert.Equal(Session, claims.SessionId);
            Assert.Equal(Host, claims.HostPlayerId);
            Assert.Equal(Client, claims.ClientPlayerId);
            Assert.Equal(now + 60, claims.ExpiresUnixSeconds);
        }

        [Fact]
        public void Introduction_token_payload_layout_is_fixed()
        {
            byte[] bytes = TestTokens.Decode(NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Client, 0x01020304));
            var expected = new byte[16];
            NatTokens.WriteGuid(Session, expected, 0);
            Assert.Equal(expected, Slice(bytes, 0, 16));
            NatTokens.WriteGuid(Host, expected, 0);
            Assert.Equal(expected, Slice(bytes, 16, 16));
            NatTokens.WriteGuid(Client, expected, 0);
            Assert.Equal(expected, Slice(bytes, 32, 16));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, Slice(bytes, 48, 4));
            Assert.True(SigningKeys.Verify(PublicOf(introducerKey), bytes, 0, 52, bytes, 52));
        }

        [Fact]
        public void Changing_any_byte_of_an_introduction_token_invalidates_it()
        {
            string token = NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Client, now + 60);
            for (int i = 0; i < NatTokens.IntroductionTokenLength; i++)
                AssertIntroductionRejected(TestTokens.FlipBit(token, i), PublicOf(introducerKey), NatTokens.InvalidToken);
        }

        [Fact]
        public void Introduction_tokens_from_another_key_or_expired_are_rejected()
        {
            AssertIntroductionRejected(NatTokens.CreateIntroductionToken(mmKey, Session, Host, Client, now + 60), PublicOf(introducerKey), NatTokens.InvalidToken);
            AssertIntroductionRejected(NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Client, now), PublicOf(introducerKey), NatTokens.ExpiredToken);
        }

        [Fact]
        public void Matchmaking_and_introduction_formats_cannot_be_confused()
        {
            string mmToken = TestTokens.Matchmaking(mmKey, Session, Client, 'C', now + 120);
            string introduction = NatTokens.CreateIntroductionToken(mmKey, Session, Host, Client, now + 60);
            AssertIntroductionRejected(mmToken, PublicOf(mmKey), NatTokens.InvalidToken);
            AssertMatchmakingRejected(introduction, NatTokens.InvalidToken);
        }

        [Fact]
        public void Introduction_tokens_reject_empty_ids_and_unrepresentable_expirations()
        {
            Assert.Throws<ArgumentException>(() => NatTokens.CreateIntroductionToken(introducerKey, Guid.Empty, Host, Client, now + 60));
            Assert.Throws<ArgumentException>(() => NatTokens.CreateIntroductionToken(introducerKey, Session, Guid.Empty, Client, now + 60));
            Assert.Throws<ArgumentException>(() => NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Guid.Empty, now + 60));
            Assert.Throws<ArgumentOutOfRangeException>(() => NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Client, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Client, (long)uint.MaxValue + 1));
            Assert.Equal(155, NatTokens.CreateIntroductionToken(introducerKey, Session, Host, Client, uint.MaxValue).Length);
        }

        private void AssertMatchmakingRejected(string token, string expectedError)
        {
            MatchmakingTokenClaims claims;
            string error;
            Assert.False(NatTokens.TryVerifyMatchmakingToken(token, PublicOf(mmKey), now, out claims, out error));
            Assert.Null(claims);
            Assert.Equal(expectedError, error);
        }

        private void AssertIntroductionRejected(string token, ECPublicKeyParameters key, string expectedError)
        {
            IntroductionTokenClaims claims;
            string error;
            Assert.False(NatTokens.TryVerifyIntroductionToken(token, key, now, out claims, out error));
            Assert.Null(claims);
            Assert.Equal(expectedError, error);
        }

        private static ECPublicKeyParameters PublicOf(ECPrivateKeyParameters key) { return SigningKeys.GetPublicKey(key); }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var result = new byte[count];
            Buffer.BlockCopy(source, offset, result, 0, count);
            return result;
        }
    }
}
