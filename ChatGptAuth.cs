// "Sign in with ChatGPT": OpenAI's documented flow for open-source and locally hosted apps.
// The person signs in on OpenAI's own page in their browser; this app never sees their password.
// Docs: https://developers.openai.com/siwc/token-sharing-open-source/sign-in
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PdfReaderApp
{
    class SignInRequired : AiError { public SignInRequired(string m) : base(m) { } }

    class ChatGptAuth
    {
        const string RealIssuer = "https://auth.openai.com";
        const string Resource = "https://api.openai.com/v1";
        const string PlanScope = "chatgpt.tokens.use.direct";
        const string Scopes = "openid profile email offline_access resource.invoke " + PlanScope;
        const string CallbackPath = "/auth/callback";
        const int PreferredPort = 1455;
        static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
        static readonly string[] TerminalRefreshErrors =
            { "invalid_grant", "invalid_refresh_token", "token_expired", "refresh_token_expired", "refresh_token_invalidated", "refresh_token_reused" };

        readonly Store store;
        readonly HttpClient http = new HttpClient();
        readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        Dictionary<string, object> session;

        public ChatGptAuth(Store store)
        {
            this.store = store;
            string saved = store.GetKey("chatgpt");
            if (saved != null)
            {
                try { session = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(saved); }
                catch (Exception) { session = null; }
            }
        }

        // For tests against a local stand-in server. Only localhost is ever accepted.
        static string TestIssuer
        {
            get
            {
                string t = Environment.GetEnvironmentVariable("PDFREADER_TEST_AUTH");
                return t != null && t.StartsWith("http://127.0.0.1:") ? t : null;
            }
        }
        static string Issuer { get { return TestIssuer ?? RealIssuer; } }

        public bool SignedIn { get { return session != null && session.ContainsKey("refresh_token"); } }
        public bool HasPlanUsage { get { return SignedIn && GrantedScopes().Contains(PlanScope); } }
        public string Email { get { return session != null && session.ContainsKey("email") ? session["email"] as string : null; } }

        IEnumerable<string> GrantedScopes()
        {
            object s;
            return session.TryGetValue("scope", out s) && s is string
                ? ((string)s).Split(' ')
                : new string[0];
        }

        string HostId()
        {
            string id = store.Get("chatgpt_host", null);
            if (id == null)
            {
                id = "urn:uuid:" + Guid.NewGuid().ToString();
                store.Set("chatgpt_host", id);
                store.Save();
            }
            return id;
        }

        static string Random64()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return Base64Url(bytes);
        }

        static string Base64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        static string Q(string s) { return Uri.EscapeDataString(s); }

        public async Task SignIn(CancellationToken cancel)
        {
            string savedClient = store.Get("chatgpt_client", null);
            string verifier = Random64(), state = Random64(), nonce = Random64();
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));

            TcpListener listener = StartListener();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string redirect = "http://127.0.0.1:" + port + CallbackPath;
                string url = Issuer + "/api/accounts/authorize?client_id=" + Q(savedClient ?? "dynamic_agent_client")
                    + (savedClient == null ? "&agent_name_hint=" + Q("PDF Reader") : "")
                    + "&ext_agent_host_id=" + Q(HostId())
                    + "&response_type=code&redirect_uri=" + Q(redirect)
                    + "&scope=" + Q(Scopes) + "&resource=" + Q(Resource)
                    + "&state=" + Q(state) + "&nonce=" + Q(nonce)
                    + "&code_challenge_method=S256&code_challenge=" + Q(challenge);

                using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel))
                {
                    limit.CancelAfter(SignInTimeout);
                    Task<Dictionary<string, string>> waiting = WaitForCallback(listener, limit.Token);
                    if (TestIssuer != null) { var ignored = http.GetAsync(url); }
                    else Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                    Dictionary<string, string> cb = await waiting;

                    string value;
                    if (!cb.TryGetValue("state", out value) || value != state) throw new AiError("Sign-in failed a safety check. Please try again.");
                    if (cb.TryGetValue("error", out value))
                        throw new AiError(value == "access_denied" ? "Sign-in was canceled." : "Sign-in failed: " + value);
                    string code;
                    if (!cb.TryGetValue("code", out code)) throw new AiError("Sign-in didn't finish. Please try again.");

                    string client;
                    cb.TryGetValue("client_id", out client);
                    if (savedClient != null && client != null && client != savedClient) throw new AiError("Sign-in returned a different app ID. Please try again.");
                    client = savedClient ?? client;
                    if (client == null) throw new AiError("Sign-in didn't finish. Please try again.");

                    Dictionary<string, object> tokens = await PostToken(new Dictionary<string, string>
                    {
                        { "grant_type", "authorization_code" }, { "client_id", client }, { "code", code },
                        { "code_verifier", verifier }, { "redirect_uri", redirect }, { "resource", Resource },
                    }, cancel);

                    string idToken = tokens.ContainsKey("id_token") ? tokens["id_token"] as string : null;
                    var claims = ReadClaims(idToken);
                    if (claims != null && claims.ContainsKey("nonce") && (claims["nonce"] as string) != nonce)
                        throw new AiError("Sign-in failed a safety check. Please try again.");

                    store.Set("chatgpt_client", client);
                    session = new Dictionary<string, object>();
                    if (claims != null && claims.ContainsKey("email")) session["email"] = claims["email"];
                    Keep(tokens);
                    if (!session.ContainsKey("scope") && cb.ContainsKey("scope")) session["scope"] = cb["scope"];
                    Save();
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        static TcpListener StartListener()
        {
            try
            {
                var preferred = new TcpListener(IPAddress.Loopback, PreferredPort);
                preferred.Start();
                return preferred;
            }
            catch (SocketException)
            {
                var any = new TcpListener(IPAddress.Loopback, 0);
                any.Start();
                return any;
            }
        }

        static async Task<Dictionary<string, string>> WaitForCallback(TcpListener listener, CancellationToken cancel)
        {
            using (cancel.Register(listener.Stop))
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); }
                    catch (Exception)
                    {
                        if (cancel.IsCancellationRequested) throw new AiError("Sign-in was canceled or timed out.");
                        throw;
                    }
                    using (client)
                    {
                        NetworkStream stream = client.GetStream();
                        string request = await ReadRequest(stream);
                        string target = request.Split(' ').Length > 1 ? request.Split(' ')[1] : "";
                        int q = target.IndexOf('?');
                        string path = q >= 0 ? target.Substring(0, q) : target;
                        if (path != CallbackPath)
                        {
                            await Respond(stream, "404 Not Found", "");
                            continue;
                        }
                        await Respond(stream, "200 OK",
                            "<!doctype html><meta charset=utf-8><title>PDF Reader</title><body style='background:#141414;color:#f4f4f4;font:16px Segoe UI,sans-serif;text-align:center;padding-top:20vh'>" +
                            "<h2>You can close this tab</h2><p style='color:#a0a0a0'>Head back to PDF Reader.</p>");
                        var result = new Dictionary<string, string>();
                        if (q >= 0)
                            foreach (string pair in target.Substring(q + 1).Split('&'))
                            {
                                int eq = pair.IndexOf('=');
                                if (eq > 0) result[WebUtility.UrlDecode(pair.Substring(0, eq))] = WebUtility.UrlDecode(pair.Substring(eq + 1));
                            }
                        return result;
                    }
                }
            }
        }

        static async Task<string> ReadRequest(NetworkStream stream)
        {
            var text = new StringBuilder();
            var buffer = new byte[4096];
            while (text.Length < 65536)
            {
                int n = await stream.ReadAsync(buffer, 0, buffer.Length);
                if (n == 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, n));
                if (text.ToString().Contains("\r\n\r\n")) break;
            }
            string all = text.ToString();
            int end = all.IndexOf("\r\n");
            return end >= 0 ? all.Substring(0, end) : all;
        }

        static async Task Respond(NetworkStream stream, string status, string html)
        {
            byte[] body = Encoding.UTF8.GetBytes(html);
            byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head, 0, head.Length);
            await stream.WriteAsync(body, 0, body.Length);
        }

        async Task<Dictionary<string, object>> PostToken(Dictionary<string, string> form, CancellationToken cancel)
        {
            HttpResponseMessage response;
            try { response = await http.PostAsync(Issuer + "/api/accounts/oauth/token", new FormUrlEncodedContent(form), cancel); }
            catch (HttpRequestException) { throw new AiError("Couldn't reach OpenAI. Check your Internet connection."); }
            using (response)
            {
                string text = await response.Content.ReadAsStringAsync();
                Dictionary<string, object> json = null;
                try { json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text); }
                catch (Exception) { }
                if (response.IsSuccessStatusCode && json != null && json.ContainsKey("access_token")) return json;
                string code = json != null && json.ContainsKey("error") ? json["error"] as string : null;
                if (code != null && TerminalRefreshErrors.Contains(code)) throw new SignInRequired("Your ChatGPT sign-in has expired. Please sign in again.");
                if ((int)response.StatusCode >= 500) throw new AiError("OpenAI is having trouble signing you in. Try again shortly.");
                throw new AiError("OpenAI didn't accept the sign-in" + (code != null ? " (" + code + ")." : "."));
            }
        }

        static Dictionary<string, object> ReadClaims(string jwt)
        {
            if (jwt == null) return null;
            string[] parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            try
            {
                string p = parts[1].Replace('-', '+').Replace('_', '/');
                p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
            }
            catch (Exception) { return null; }
        }

        // Access token, expiry, scopes and the rotating refresh token are replaced together.
        void Keep(Dictionary<string, object> tokens)
        {
            session["access_token"] = tokens["access_token"];
            if (tokens.ContainsKey("refresh_token")) session["refresh_token"] = tokens["refresh_token"];
            if (tokens.ContainsKey("scope")) session["scope"] = tokens["scope"];
            int seconds = tokens.ContainsKey("expires_in") ? Convert.ToInt32(tokens["expires_in"]) : 3600;
            session["expires_utc"] = DateTime.UtcNow.AddSeconds(seconds).ToString("o");
        }

        void Save()
        {
            store.SetKey("chatgpt", new JavaScriptSerializer().Serialize(session));
            store.Save();
        }

        void Forget()
        {
            session = null;
            store.RemoveKey("chatgpt");
            store.Save();
        }

        public async Task<string> GetAccessToken(CancellationToken cancel)
        {
            await refreshGate.WaitAsync(cancel);
            try
            {
                if (!SignedIn) throw new SignInRequired("Please sign in with ChatGPT.");
                DateTime expires;
                if (DateTime.TryParse(session["expires_utc"] as string, null, System.Globalization.DateTimeStyles.RoundtripKind, out expires)
                    && expires.ToUniversalTime() > DateTime.UtcNow.AddSeconds(90))
                    return (string)session["access_token"];
                try
                {
                    Dictionary<string, object> tokens = await PostToken(new Dictionary<string, string>
                    {
                        { "grant_type", "refresh_token" }, { "client_id", store.Get("chatgpt_client", "") },
                        { "refresh_token", (string)session["refresh_token"] }, { "resource", Resource },
                    }, cancel);
                    Keep(tokens);
                    Save();
                    return (string)session["access_token"];
                }
                catch (SignInRequired)
                {
                    Forget();
                    throw;
                }
            }
            finally { refreshGate.Release(); }
        }

        public void SessionRejected() { Forget(); }

        public async Task SignOut()
        {
            string refresh = SignedIn ? session["refresh_token"] as string : null;
            string client = store.Get("chatgpt_client", null);
            Forget();
            if (refresh == null || client == null) return;
            try
            {
                string config = await http.GetStringAsync(Issuer + "/.well-known/openid-configuration");
                var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(config);
                string endpoint = json["revocation_endpoint"] as string;
                using (await http.PostAsync(endpoint, new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    { "token", refresh }, { "token_type_hint", "refresh_token" }, { "client_id", client },
                }))) { }
            }
            catch (Exception) { } // Signed out locally either way; the token also lapses after 30 days unused.
        }
    }
}
