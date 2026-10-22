using System;
using Org.BouncyCastle.Crypto.Parameters;

namespace NatTest.Common
{
    // MatchmakingService host (H) or client (C) token, signed with MM's NAT key.
    public sealed class MatchmakingTokenClaims
    {
        public MatchmakingTokenClaims(Guid sessionId, Guid playerId, char role, long expiresUnixSeconds)
        {
            SessionId = sessionId;
            PlayerId = playerId;
            Role = role;
            ExpiresUnixSeconds = expiresUnixSeconds;
        }

        public Guid SessionId { get; private set; }
        public Guid PlayerId { get; private set; }
        public char Role { get; private set; }
        public long ExpiresUnixSeconds { get; private set; }
    }

    // Introduction token, signed with the introducer's key and passed to both peers through NatIntroduce.
    public sealed class IntroductionTokenClaims
    {
        public IntroductionTokenClaims(Guid sessionId, Guid hostPlayerId, Guid clientPlayerId, long expiresUnixSeconds)
        {
            SessionId = sessionId;
            HostPlayerId = hostPlayerId;
            ClientPlayerId = clientPlayerId;
            ExpiresUnixSeconds = expiresUnixSeconds;
        }

        public Guid SessionId { get; private set; }
        public Guid HostPlayerId { get; private set; }
        public Guid ClientPlayerId { get; private set; }
        public long ExpiresUnixSeconds { get; private set; }
    }

    // Both token formats are fixed-width binary, encoded as unpadded Base64URL. GUIDs use RFC 4122 big-endian byte order
    // (the hex digits of the "D" string in order) and expirations are unsigned 32-bit big-endian Unix seconds.
    //
    // MatchmakingService token, 101 bytes / 135 characters (contract in MatchmakingService README):
    //   0 session ID (16) | 16 player ID (16) | 32 role 'H', 'C' or 'O' (1) | 33 expiration (4) | 37 ES256 R || S over bytes 0..36 (64)
    //   O (owner) tokens have an all-zero session ID
    //
    // Introduction token, 116 bytes / 155 characters:
    //   0 session ID (16) | 16 host player ID (16) | 32 client player ID (16) | 48 expiration (4) | 52 ES256 R || S over bytes 0..51 (64)
    //
    // The two formats are told apart by length and are signed with different keys, so neither can be accepted as the other.
    public static class NatTokens
    {
        public const char HostRole = 'H';
        public const char ClientRole = 'C';
        // MM's owner token: proves the account to a game server the player owns, has no session, never valid for NAT
        public const char OwnerRole = 'O';
        public const int LiteNetLibMaxTokenLength = 256;

        public const int MatchmakingPayloadLength = 37;
        public const int MatchmakingTokenLength = MatchmakingPayloadLength + SigningKeys.SignatureLength;
        public const int IntroductionPayloadLength = 52;
        public const int IntroductionTokenLength = IntroductionPayloadLength + SigningKeys.SignatureLength;

        public const string InvalidToken = "invalid-token";
        public const string ExpiredToken = "token-expired";

        private const int GuidLength = 16;

        public static long NowUnixSeconds() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }

        // Reads a peer's own MatchmakingService token without checking the signature, e.g. so a client learns its session and
        // player ID. Never use this to authorize anything.
        public static bool TryReadMatchmakingToken(string token, out MatchmakingTokenClaims claims)
        {
            claims = null;
            byte[] bytes;
            if (!TryDecode(token, MatchmakingTokenLength, out bytes)) return false;
            char role = (char)bytes[32];
            long expires = ReadUInt32BigEndian(bytes, 33);
            Guid session = ReadGuid(bytes, 0);
            Guid player = ReadGuid(bytes, GuidLength);
            bool validRole = role == OwnerRole ? session == Guid.Empty : (role == HostRole || role == ClientRole) && session != Guid.Empty;
            if (!validRole || expires == 0 || player == Guid.Empty) return false;
            claims = new MatchmakingTokenClaims(session, player, role, expires);
            return true;
        }

        // Verifies the signature and expiry. The caller must still check that the role and session match the operation.
        public static bool TryVerifyMatchmakingToken(string token, ECPublicKeyParameters matchmakingKey, long nowUnixSeconds, out MatchmakingTokenClaims claims, out string error)
        {
            claims = null;
            error = InvalidToken;
            MatchmakingTokenClaims parsed;
            byte[] bytes;
            if (!TryReadMatchmakingToken(token, out parsed) || !TryDecode(token, MatchmakingTokenLength, out bytes) ||
                !SigningKeys.Verify(matchmakingKey, bytes, 0, MatchmakingPayloadLength, bytes, MatchmakingPayloadLength)) return false;
            if (parsed.ExpiresUnixSeconds <= nowUnixSeconds)
            {
                error = ExpiredToken;
                return false;
            }
            claims = parsed;
            error = null;
            return true;
        }

        public static string CreateIntroductionToken(ECPrivateKeyParameters introducerKey, Guid sessionId, Guid hostPlayerId, Guid clientPlayerId, long expiresUnixSeconds)
        {
            if (sessionId == Guid.Empty || hostPlayerId == Guid.Empty || clientPlayerId == Guid.Empty) throw new ArgumentException("Session and player IDs are required.");
            if (expiresUnixSeconds <= 0 || expiresUnixSeconds > uint.MaxValue)
                throw new ArgumentOutOfRangeException("expiresUnixSeconds", "The expiration cannot be represented as unsigned 32-bit Unix seconds.");

            var bytes = new byte[IntroductionTokenLength];
            WriteGuid(sessionId, bytes, 0);
            WriteGuid(hostPlayerId, bytes, GuidLength);
            WriteGuid(clientPlayerId, bytes, 2 * GuidLength);
            WriteUInt32BigEndian((uint)expiresUnixSeconds, bytes, 48);
            Buffer.BlockCopy(SigningKeys.Sign(introducerKey, bytes, 0, IntroductionPayloadLength), 0, bytes, IntroductionPayloadLength, SigningKeys.SignatureLength);
            return Base64UrlEncode(bytes);
        }

        // Verifies the signature and expiry. The caller must still check that the session and both player IDs are the expected ones.
        public static bool TryVerifyIntroductionToken(string token, ECPublicKeyParameters introducerKey, long nowUnixSeconds, out IntroductionTokenClaims claims, out string error)
        {
            claims = null;
            error = InvalidToken;
            byte[] bytes;
            if (!TryDecode(token, IntroductionTokenLength, out bytes) ||
                !SigningKeys.Verify(introducerKey, bytes, 0, IntroductionPayloadLength, bytes, IntroductionPayloadLength)) return false;
            long expires = ReadUInt32BigEndian(bytes, 48);
            if (expires <= nowUnixSeconds)
            {
                error = ExpiredToken;
                return false;
            }
            claims = new IntroductionTokenClaims(ReadGuid(bytes, 0), ReadGuid(bytes, GuidLength), ReadGuid(bytes, 2 * GuidLength), expires);
            error = null;
            return true;
        }

        internal static void WriteGuid(Guid value, byte[] destination, int offset)
        {
            // Guid.ToByteArray stores the first three fields little-endian; reverse them to get RFC 4122 order.
            byte[] b = value.ToByteArray();
            byte[] order = { 3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15 };
            for (int i = 0; i < GuidLength; i++) destination[offset + i] = b[order[i]];
        }

        internal static Guid ReadGuid(byte[] source, int offset)
        {
            var b = new byte[GuidLength];
            byte[] order = { 3, 2, 1, 0, 5, 4, 7, 6, 8, 9, 10, 11, 12, 13, 14, 15 };
            for (int i = 0; i < GuidLength; i++) b[order[i]] = source[offset + i];
            return new Guid(b);
        }

        internal static string Base64UrlEncode(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // Accepts only the canonical unpadded Base64URL encoding of exactly byteLength bytes.
        internal static bool TryDecode(string value, int byteLength, out byte[] bytes)
        {
            bytes = null;
            if (value == null || value.Length != (byteLength * 4 + 2) / 3) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!(c >= 'A' && c <= 'Z') && !(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-' && c != '_') return false;
            }
            try
            {
                string base64 = value.Replace('-', '+').Replace('_', '/');
                byte[] decoded = Convert.FromBase64String(base64 + new string('=', (4 - base64.Length % 4) % 4));
                // Re-encoding rejects strings whose unused trailing bits are not zero.
                if (decoded.Length != byteLength || Base64UrlEncode(decoded) != value) return false;
                bytes = decoded;
                return true;
            }
            catch (FormatException) { return false; }
        }

        private static long ReadUInt32BigEndian(byte[] source, int offset)
        {
            return ((long)source[offset] << 24) | ((long)source[offset + 1] << 16) | ((long)source[offset + 2] << 8) | source[offset + 3];
        }

        private static void WriteUInt32BigEndian(uint value, byte[] destination, int offset)
        {
            destination[offset] = (byte)(value >> 24);
            destination[offset + 1] = (byte)(value >> 16);
            destination[offset + 2] = (byte)(value >> 8);
            destination[offset + 3] = (byte)value;
        }
    }
}
