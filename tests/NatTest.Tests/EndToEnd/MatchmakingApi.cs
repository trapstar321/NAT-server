using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace NatTest.Tests.EndToEnd
{
    internal sealed class Account
    {
        public Guid PlayerId;
        public string AuthToken;
    }

    internal sealed class CreatedGame
    {
        public Guid SessionId;
        public Guid HostPlayerId;
        public string Token;
    }

    // The parts of the MatchmakingService HTTP API that a game host and client use.
    internal sealed class MatchmakingApi : IDisposable
    {
        private readonly HttpClient http;

        internal MatchmakingApi(Uri baseAddress)
        {
            http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        }

        internal async Task<bool> IsHealthy()
        {
            try
            {
                using (var response = await http.GetAsync("/healthz")) return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException) { return false; }
            catch (TaskCanceledException) { return false; }
        }

        // Registers the account, or logs in if it already exists.
        internal async Task<Account> RegisterOrLogin(string email, string password)
        {
            var credentials = new CredentialsBody { Email = email, Password = password };
            using (var response = await Post("/accounts/register", credentials, null))
            {
                if (response.StatusCode == HttpStatusCode.Created) return await ReadAccount(response);
                if (response.StatusCode != HttpStatusCode.Conflict) throw await Unexpected("register", response);
            }
            using (var response = await Post("/accounts/login", credentials, null))
            {
                if (response.StatusCode != HttpStatusCode.OK) throw await Unexpected("login", response);
                return await ReadAccount(response);
            }
        }

        internal async Task<CreatedGame> CreateGame(Account host, string gameType, string secret)
        {
            var body = new CreateGameBody { PlayerName = "E2E host", GameName = "NAT end-to-end", GameType = gameType, Secret = secret };
            using (var response = await Post("/games", body, host))
            {
                if (response.StatusCode != HttpStatusCode.Created) throw await Unexpected("create game", response);
                var created = Read<CreateGameResult>(await response.Content.ReadAsStringAsync());
                return new CreatedGame { SessionId = Guid.Parse(created.SessionId), HostPlayerId = Guid.Parse(created.HostPlayerId), Token = created.Token };
            }
        }

        internal async Task<string> OwnerToken(Account player)
        {
            using (var response = await Post("/accounts/owner-token", new EmptyBody(), player))
            {
                if (response.StatusCode != HttpStatusCode.OK) throw await Unexpected("owner token", response);
                return Read<JoinResult>(await response.Content.ReadAsStringAsync()).Token;
            }
        }

        internal Task<HttpResponseMessage> JoinRaw(Account player, Guid session, string secret)
        {
            return Post("/join", new JoinBody { Session = session.ToString(), Secret = secret }, player);
        }

        internal async Task<string> Join(Account player, Guid session, string secret = null)
        {
            using (var response = await JoinRaw(player, session, secret))
            {
                if (response.StatusCode != HttpStatusCode.OK) throw await Unexpected("join", response);
                return Read<JoinResult>(await response.Content.ReadAsStringAsync()).Token;
            }
        }

        public void Dispose() { http.Dispose(); }

        private Task<HttpResponseMessage> Post<T>(string path, T body, Account account)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(Write(body), Encoding.UTF8, "application/json") };
            if (account != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AuthToken);
            return http.SendAsync(request);
        }

        private static async Task<Account> ReadAccount(HttpResponseMessage response)
        {
            var auth = Read<AuthResult>(await response.Content.ReadAsStringAsync());
            return new Account { PlayerId = Guid.Parse(auth.PlayerId), AuthToken = auth.AuthToken };
        }

        private static async Task<Exception> Unexpected(string operation, HttpResponseMessage response)
        {
            return new XunitException("MatchmakingService " + operation + " returned " + (int)response.StatusCode + ": " + await response.Content.ReadAsStringAsync());
        }

        private static string Write<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static T Read<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

#pragma warning disable 0649 // Response fields are assigned by DataContractJsonSerializer.
        [DataContract]
        private sealed class CredentialsBody
        {
            [DataMember(Name = "email")] public string Email;
            [DataMember(Name = "password")] public string Password;
        }

        [DataContract]
        private sealed class CreateGameBody
        {
            [DataMember(Name = "playerName")] public string PlayerName;
            [DataMember(Name = "gameName")] public string GameName;
            [DataMember(Name = "gameType")] public string GameType;
            [DataMember(Name = "secret", EmitDefaultValue = false)] public string Secret;
        }

        [DataContract]
        private sealed class EmptyBody
        {
        }

        [DataContract]
        private sealed class JoinBody
        {
            [DataMember(Name = "session")] public string Session;
            [DataMember(Name = "secret", EmitDefaultValue = false)] public string Secret;
        }

        [DataContract]
        private sealed class AuthResult
        {
            [DataMember(Name = "playerId")] public string PlayerId;
            [DataMember(Name = "authToken")] public string AuthToken;
        }

        [DataContract]
        private sealed class CreateGameResult
        {
            [DataMember(Name = "sessionId")] public string SessionId;
            [DataMember(Name = "hostPlayerId")] public string HostPlayerId;
            [DataMember(Name = "token")] public string Token;
        }

        [DataContract]
        private sealed class JoinResult
        {
            [DataMember(Name = "token")] public string Token;
        }
#pragma warning restore 0649
    }
}
