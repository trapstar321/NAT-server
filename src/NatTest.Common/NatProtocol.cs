using System;
namespace NatTest.Common
{
    /// <summary>Console-free wire labels shared by the Unity-ready networking layer and test executables.</summary>
    public static class NatProtocol
    {
        public const string ConnectionKey = "NATTEST/1";
        public const string Hello = "NATTEST/1|HELLO";
        public const string Ready = "NATTEST/1|READY";
        public const string MessagePrefix = "NATTEST/1|MSG|";
        public const string ReplyPrefix = "NATTEST/1|REPLY|";
        public static string HostToken(string session) { return "H|" + ValidateSession(session); }
        public static string ClientToken(string session) { return "C|" + ValidateSession(session); }
        public static bool TryParseToken(string token, out char role, out string session)
        {
            role = '\0'; session = null;
            if (String.IsNullOrEmpty(token) || token.Length > 200) return false;
            int split = token.IndexOf('|');
            if (split != 1 || split == token.Length - 1) return false;
            role = token[0]; session = token.Substring(2);
            return (role == 'H' || role == 'C') && session.Length <= 120;
        }
        public static string ValidateSession(string session)
        {
            if (String.IsNullOrWhiteSpace(session) || session.Length > 120 || session.IndexOf('|') >= 0)
                throw new ArgumentException("Session must be 1-120 characters and cannot contain '|'.", "session");
            return session;
        }
        public static bool IsApplicationMessage(string message) { return message != null && message.StartsWith("NATTEST/1|", StringComparison.Ordinal); }
    }
}
