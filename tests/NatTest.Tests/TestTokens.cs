using System;
using NatTest.Common;
using Org.BouncyCastle.Crypto.Parameters;

namespace NatTest.Tests
{
    // Mints tokens in MatchmakingService's layout so tests can cover cases MM will not issue (expired, other players, other keys).
    internal static class TestTokens
    {
        internal static string Matchmaking(ECPrivateKeyParameters key, Guid session, Guid player, char role, long expires)
        {
            var bytes = new byte[NatTokens.MatchmakingTokenLength];
            NatTokens.WriteGuid(session, bytes, 0);
            NatTokens.WriteGuid(player, bytes, 16);
            bytes[32] = (byte)role;
            bytes[33] = (byte)(expires >> 24);
            bytes[34] = (byte)(expires >> 16);
            bytes[35] = (byte)(expires >> 8);
            bytes[36] = (byte)expires;
            Buffer.BlockCopy(SigningKeys.Sign(key, bytes, 0, NatTokens.MatchmakingPayloadLength), 0, bytes, NatTokens.MatchmakingPayloadLength, SigningKeys.SignatureLength);
            return NatTokens.Base64UrlEncode(bytes);
        }

        internal static byte[] Decode(string token)
        {
            string base64 = token.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
        }

        internal static string FlipBit(string token, int byteIndex)
        {
            byte[] bytes = Decode(token);
            bytes[byteIndex] ^= 0x01;
            return NatTokens.Base64UrlEncode(bytes);
        }
    }
}
