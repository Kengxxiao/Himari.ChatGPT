using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http.Json;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;
using static Himari.ChatGPT.ChatGPTObject;

namespace Himari.ChatGPT
{
    public class ChatGPTObject
    {
        public class Message
        {
            [JsonPropertyName("id")]
            public string Id { get; set; }
            [JsonPropertyName("role")]
            public string Role { get; set; }
            [JsonPropertyName("content")]
            public MessageContent Content { get; set; }
        }

        public class MessageContent
        {
            [JsonPropertyName("content_type")]
            public string ContentType { get; set; }
            [JsonPropertyName("parts")]
            public List<string> Parts { get; set; }
        }

        public class ConversationRequest
        {
            [JsonPropertyName("action")]
            public string Action { get; set; }
            [JsonPropertyName("messages")]
            public List<Message> Message { get; set; }
            [JsonPropertyName("parent_message_id")]
            public string ParentMessageId { get; set; }
            [JsonPropertyName("model")]
            public string Model { get; set; }
            [JsonPropertyName("conversation_id")]
            public string? ConversationId { get; set; }
        }

        public class ConversationStatus
        {
            public string? ConversationId { get; set; }
            public string? ParentMessageId { get; set; }
            public bool IsCompleted { get; set; }
        }

        public class ConversationResponse
        {
            [JsonPropertyName("conversation_id")]
            public string? ConversationId { get; set; }
            [JsonPropertyName("error")]
            public string? Error { get; set; }
            [JsonPropertyName("message")]
            public Message Message { get; set; }
        }
    }
    public class ChatGPTClient
    {
        private IHttpClientFactory _httpClientFactory;
        private IConfiguration _config;
        private ILogger<ChatGPTClient> _logger;
        private string _accessToken;

        private Dictionary<long, ConversationStatus> UserConversation = new();

        public ChatGPTClient(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ChatGPTClient> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        public bool GetUserCompleted(long userId)
        {
            if (!UserConversation.ContainsKey(userId) || UserConversation[userId].IsCompleted)
                return true;
            return false;
        }

        public async Task RequestConversation(long userId, string part, Action<string, Exception?> callback, CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient("ChatGPT");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/107.0.0.0 Safari/537.36 Edg/107.0.1418.62");
            client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);
            string? conversationId = null;
            string? parentMessageId;
            if (!UserConversation.TryGetValue(userId, out var save))
            {
                parentMessageId = Guid.NewGuid().ToString("D").ToLower();
                UserConversation[userId] = new ConversationStatus
                {
                    ConversationId = null,
                    ParentMessageId = parentMessageId,
                    IsCompleted = false
                };
            }
            else
            {
                parentMessageId = save.ParentMessageId;
                conversationId = save.ConversationId;
            }
            string? currId = Guid.NewGuid().ToString("D").ToLower();

            UserConversation[userId].IsCompleted = false;
            try
            {
                string lastMessage = string.Empty;
                var request = new ConversationRequest
                {
                    Action = "next",
                    Message = new List<Message>
                    {
                        new Message
                        {
                            Id = currId,
                            Role = "user",
                            Content = new MessageContent
                            {
                                ContentType = "text",
                                Parts = new List<string> { part }
                            }
                        }
                    },
                    ConversationId = conversationId,
                    ParentMessageId = parentMessageId,
                    Model = "text-davinci-002-render"
                };
                var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://chat.openai.com/backend-api/conversation")
                {
                    Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
                };
                var stream = await client.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                stream.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync(cancellationToken), Encoding.UTF8);
                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    if (line != null && line.StartsWith("data: "))
                    {
                        var data = line["data: ".Length..];
                        if (data == "[DONE]")
                        {
                            _logger.LogInformation("读取新数据：{ConversationId}, {ParentMessageId}, {Message}", UserConversation[userId].ConversationId, UserConversation[userId].ParentMessageId, lastMessage);
                            callback(lastMessage, null);
                        }
                        else if (data.StartsWith("{"))
                        {
                            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(data));
                            var response = await JsonSerializer.DeserializeAsync<ConversationResponse>(ms, cancellationToken: cancellationToken);
                            if (response.Message.Content.Parts.Any())
                                lastMessage = response.Message.Content.Parts[0];
                            UserConversation[userId].ParentMessageId = response.Message.Id;
                            UserConversation[userId].ConversationId = response.ConversationId;
                            //_logger.LogInformation("读取新数据：{ConversationId}, {ParentMessageId}, {Message}", response.ConversationId, response.Message.Id, lastMessage);
                        }
                    }
                }
                UserConversation[userId].IsCompleted = true;
            }
            catch (Exception e)
            {
                UserConversation[userId].IsCompleted = true;
                callback(string.Empty, e);
            }
        }

        public void AuthLoginWithAccessToken(string accessToken)
        {
            _accessToken = accessToken;
        }

        public async Task<bool> AuthLogin(string username, string password, Action<string, Exception?> callback, CancellationToken cancellationToken)
        {
            HttpResponseMessage loginHtml = null;

            var client = _httpClientFactory.CreateClient("ChatGPT");
            client.DefaultRequestHeaders.Add("Connection", "keep-alive");
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/107.0.0.0 Safari/537.36 Edg/107.0.1418.62");

            try
            {
                using var authLogin = await client.GetAsync("https://chat.openai.com/auth/login", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                authLogin.EnsureSuccessStatusCode();

                using var csrf = await client.GetAsync("https://chat.openai.com/api/auth/csrf", cancellationToken);
                csrf.EnsureSuccessStatusCode();

                var csrfJson = await JsonSerializer.DeserializeAsync<JsonElement>(await csrf.Content.ReadAsStreamAsync(), cancellationToken: cancellationToken);
                var csrfToken = csrfJson.GetProperty("csrfToken").GetString();

                using var signInAuth0Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"callbackUrl", "/" },
                    {"csrfToken", csrfToken },
                    {"json", "true" }
                });
                var signInAuth0 = await client.PostAsync("https://chat.openai.com/api/auth/signin/auth0?prompt=login", signInAuth0Content, cancellationToken);
                signInAuth0.EnsureSuccessStatusCode();

                var signInAuth0Json = await JsonSerializer.DeserializeAsync<JsonElement>(await signInAuth0.Content.ReadAsStreamAsync(), cancellationToken: cancellationToken);
                var url = signInAuth0Json.GetProperty("url").GetString();

                var uri = new Uri(url);
                var queryParams = HttpUtility.ParseQueryString(uri.Query);
                var state = queryParams["state"];

                using var authorize = await client.GetAsync($"https://auth0.openai.com/authorize?client_id={queryParams["client_id"]}&scope=openid%20email%20profile%20offline_access%20model.request%20model.read%20organization.read&response_type=code&redirect_uri=https%3A%2F%2Fchat.openai.com%2Fapi%2Fauth%2Fcallback%2Fauth0&audience=https%3A%2F%2Fapi.openai.com%2Fv1&prompt=login&state={state}&code_challenge={queryParams["code_challenge"]}&code_challenge_method={queryParams["code_challenge_method"]}", cancellationToken: cancellationToken);
                if (authorize.StatusCode == System.Net.HttpStatusCode.Found)
                {
                    var location = authorize.Headers.GetValues("Location").First();
                    uri = new Uri($"https://auth0.openai.com{location}");
                    state = HttpUtility.ParseQueryString(uri.Query)["state"];
                }
                else
                    authorize.EnsureSuccessStatusCode();

                using var loginIdentifierContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"state", state },
                    {"username", username },
                    {"js-available", "false" },
                    {"webauthn-available", "false" },
                    {"is-brave", "true" },
                    {"webauthn-platform-available", "false" },
                    {"action", "default" }
                });
                using var loginIdentifier = await client.PostAsync($"https://auth0.openai.com/u/login/identifier?state={state}", loginIdentifierContent, cancellationToken);
                if (loginIdentifier.StatusCode == System.Net.HttpStatusCode.Found)
                {
                    var location = loginIdentifier.Headers.GetValues("Location").First();
                    uri = new Uri($"https://auth0.openai.com{location}");
                }
                else
                    loginIdentifier.EnsureSuccessStatusCode();

                using var loginContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    {"state", state },
                    {"username", username },
                    {"password", password },
                    {"action", "default" }
                });
                loginHtml = await client.PostAsync(uri, loginContent, cancellationToken);
                if (loginHtml.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    callback("账号或密码错误", null);
                    return false;
                }
                else
                {
                    while (loginHtml.StatusCode == System.Net.HttpStatusCode.Found || loginHtml.StatusCode == System.Net.HttpStatusCode.TemporaryRedirect)
                    {
                        var location = loginHtml.Headers.GetValues("Location").First();
                        if (location.StartsWith("/"))
                            uri = new Uri($"https://{uri.DnsSafeHost}{location}");
                        else
                            uri = new Uri(location);
                        queryParams = HttpUtility.ParseQueryString(uri.ToString());
                        loginHtml.Dispose();
                        if (queryParams.AllKeys.Contains("error"))
                        {
                            callback($"错误：{queryParams["error"]}", null);
                            return false;
                        }
                        loginHtml = await client.GetAsync(uri, cancellationToken);
                    }
                }
                /*
                var doc = new HtmlDocument();
                doc.LoadHtml(await loginHtml.Content.ReadAsStringAsync(cancellationToken));
                var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@id='__NEXT_DATA__']");
                if (scriptNode == null)
                    loginHtml.EnsureSuccessStatusCode();
                using var scriptStream = new MemoryStream(Encoding.UTF8.GetBytes(scriptNode.InnerText));
                var scriptContent = await JsonSerializer.DeserializeAsync<JsonElement>(scriptStream, cancellationToken: cancellationToken);
                var accessToken = scriptContent.GetProperty("props").GetProperty("pageProps").GetProperty("accessToken").GetString();
                */

                using var authRequest = await client.GetAsync("https://chat.openai.com/api/auth/session", cancellationToken);
                var authJson = await JsonSerializer.DeserializeAsync<JsonElement>(await authRequest.Content.ReadAsStreamAsync(), cancellationToken: cancellationToken);
                _accessToken = authJson.GetProperty("accessToken").GetString();

                _logger.LogInformation("ChatGPT AccessToken: {accessToken}", _accessToken);
                return true;
            }
            catch (Exception e)
            {
                if (loginHtml != null)
                    loginHtml.Dispose();
                callback(string.Empty, e);
            }
            return false;
        }
    }
}
