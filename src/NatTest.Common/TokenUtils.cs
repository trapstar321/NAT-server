using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace NatTest.Common
{
    public static class TokenUtils
    {
        public static bool TryParseToken(string token, string signingKey, out char role, out string session, out long expires, out string errorCode)
        {
            role = '\0'; session = null; expires = 0;
            errorCode = "invalid-token";
            if (String.IsNullOrEmpty(token) || token.Length > 4096 || String.IsNullOrWhiteSpace(signingKey) || Encoding.UTF8.GetByteCount(signingKey) < 32) return false;
            string[] parts = token.Split('.');
            byte[] payloadBytes, suppliedSignature;
            if (parts.Length != 2 || !TryDecodeBase64Url(parts[0], out payloadBytes) || !TryDecodeBase64Url(parts[1], out suppliedSignature)) return false;
            try
            {
                byte[] expectedSignature;
                using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey)))
                    expectedSignature = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0]));
                if (!FixedTimeEquals(suppliedSignature, expectedSignature)) return false;

                TokenPayload claims;
                var serializer = new DataContractJsonSerializer(typeof(TokenPayload));
                using (var stream = new MemoryStream(payloadBytes)) claims = (TokenPayload)serializer.ReadObject(stream);
                if (claims == null || String.IsNullOrWhiteSpace(claims.Session) || claims.Session.Length > 120 || claims.Session.IndexOf('|') >= 0 ||
                    (claims.Role != "H" && claims.Role != "C")) return false;

                if (claims.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                {
                    errorCode = "token-expired";
                    return false;
                }

                role = claims.Role[0];
                session = claims.Session;
                expires = claims.Expires;
                errorCode = null;
                return true;
            }
            catch (Exception) { return false; }
        }

        private static bool TryDecodeBase64Url(string value, out byte[] bytes)
        {
            bytes = null;
            if (String.IsNullOrEmpty(value) || value.Length % 4 == 1) return false;
            for (int i = 0; i < value.Length; i++)
                if (!(value[i] >= 'A' && value[i] <= 'Z') && !(value[i] >= 'a' && value[i] <= 'z') && !(value[i] >= '0' && value[i] <= '9') && value[i] != '-' && value[i] != '_') return false;
            try
            {
                string base64 = value.Replace('-', '+').Replace('_', '/');
                base64 += new string('=', (4 - base64.Length % 4) % 4);
                bytes = Convert.FromBase64String(base64);
                return true;
            }
            catch (FormatException) { return false; }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        [DataContract]
        private sealed class TokenPayload
        {
            [DataMember(Name = "session")] public string Session { get; set; }
            [DataMember(Name = "role")] public string Role { get; set; }
            [DataMember(Name = "expires")] public long Expires { get; set; }
        }
    }
}
