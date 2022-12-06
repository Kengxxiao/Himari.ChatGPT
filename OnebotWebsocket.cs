using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Himari.ChatGPT
{
    public class OnebotWebsocket : BackgroundService
    {
        private IConfiguration _config;
        private ILogger<OnebotWebsocket> _logger;
        private ChatGPTClient _gpt;
        public OnebotWebsocket(ChatGPTClient gpt, IConfiguration config, ILogger<OnebotWebsocket> logger)
        {
            _config = config;
            _logger = logger;
            _gpt = gpt;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var username = _config.GetValue("OAIUsername", string.Empty);
            var password = _config.GetValue("OAIPassword", string.Empty);

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                _logger.LogWarning("请在配置文件中填写要登录的OpenAI ChatGPT账号和密码");
                return;
            }

            var loginResult = await _gpt.AuthLogin(username, password, (str, e) =>
            {
                if (!string.IsNullOrEmpty(str))
                    _logger.LogWarning(str);
                if (e != null)
                    _logger.LogWarning(e.Message);
            }, stoppingToken);
            if (!loginResult)
            {
                _logger.LogWarning("登录失败，请重新登录");
                return;
            }

            var ipAddress = IPAddress.Parse(_config.GetValue("BindIp", "127.0.0.1") ?? "127.0.0.1");
            var bindPort = _config.GetValue("BindPort", 8085);

            _logger.LogInformation("准备启动OnebotWebsocket于{Ip}:{Port}...", ipAddress, bindPort);

            var server = new HttpListener();
            server.Prefixes.Add($"http://{ipAddress}:{bindPort}/");
            server.Start();

            var listener = Task.Run(() =>
            {
                while (true)
                {
                    var context = server.GetContext();
                    if (context.Request.IsWebSocketRequest)
                        ProcessRequest(context);
                    else
                    {
                        context.Response.StatusCode = 404;
                        context.Response.Close();
                    }
                }
            }, stoppingToken);
        }

        private async void ProcessRequest(HttpListenerContext context)
        {
            context.Response.Headers.Set("User-Agent", "Himari/3 Himari.ChatGPT/1");

            var accessToken = _config.GetValue("AccessToken", string.Empty);
            if (!string.IsNullOrEmpty(accessToken))
            {
                if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Get("Token"), out var authorization))
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    _logger.LogWarning("您在配置中定义了Authorization，但您的OneBot客户端没有传递它。");
                    return;
                }
                //_logger.LogInformation("传递的Authorization: {auth} {schema} {accessToken}", authorization.Parameter, authorization.Scheme, accessToken);
                if (authorization.Scheme != "Bearer" || authorization.Parameter != accessToken)
                {
                    context.Response.StatusCode = 401;
                    context.Response.Close();
                    _logger.LogWarning("您在配置中定义了Authorization，但您的OneBot客户端传递了非法的值。");
                    return;
                }
            }
            /*
            var subProtocols = context.Request.Headers.Get("Sec-WebSocket-Protocol");
            if (string.IsNullOrEmpty(subProtocols))
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                _logger.LogWarning("客户端没有合法的子协议。");
                return;
            }

            var validProtocol = subProtocols.Split(",").Any(x => x.Trim() == "12.go-cqhttp");
            if (!validProtocol)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                _logger.LogWarning("客户端不支持OneBot 12协议。");
                return;
            }*/

            var webSocketContext = await context.AcceptWebSocketAsync(null!);
            var webSocket = webSocketContext.WebSocket;
            var buffer = new byte[1024];
            _logger.LogInformation("已与客户端{address}建立连接。", context.Request.RemoteEndPoint);
            while (webSocketContext.WebSocket != null && webSocketContext.WebSocket.State != WebSocketState.Closed && webSocketContext.WebSocket.State != WebSocketState.Aborted)
            {
                WebSocketReceiveResult result;
                using var ms = new MemoryStream();
                try
                {
                    do
                    {
                        result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                        ms.Write(buffer, 0, result.Count);
                    } while (!result.EndOfMessage);
                }
                catch (Exception e)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.InternalServerError, e.Message, CancellationToken.None);
                    _logger.LogInformation("与客户端{address}的连接异常中断，原因：{exp}。", context.Request.RemoteEndPoint, e.Message);
                    return;
                }

                ms.Seek(0, SeekOrigin.Begin);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                    _logger.LogInformation("与客户端{address}的连接已结束。", context.Request.RemoteEndPoint);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var requestJson = await JsonSerializer.DeserializeAsync<JsonElement>(ms, cancellationToken: CancellationToken.None);
                    if (!requestJson.TryGetProperty("post_type", out var postTypeProperty))
                        continue;
                    var postType = postTypeProperty.GetString();
                    //_logger.LogInformation(requestJson.ToString());
                    switch (postType)
                    {
                        case "meta_event":
                            var metaEventType = requestJson.GetProperty("meta_event_type").GetString();
                            if (metaEventType == "lifecycle")
                            {
                                var subType = requestJson.GetProperty("sub_type").GetString();
                                if (subType == "connect")
                                    _logger.LogWarning("已连接到账号{QQ}", requestJson.GetProperty("self_id").GetInt64());
                            }
                            else if (metaEventType == "heartbeat")
                            {
                                _logger.LogInformation("心跳");
                            }
                            break;
                        case "message":
                            var messageType = requestJson.GetProperty("message_type").GetString();
                            if (messageType == "group")
                            {
                                var messageSubType = requestJson.GetProperty("sub_type").GetString();
                                if (messageSubType == "normal")
                                {
                                    var message = requestJson.GetProperty("message").EnumerateArray().Where(x => x.GetProperty("type").GetString() == "text").Select(x => x.GetProperty("data").GetProperty("text").GetString().Trim());
                                    var rawMessage = string.Join("", message);

                                    var match = Regex.Match(rawMessage, "^\\/chat\\s+(.+)");
                                    if (match.Success)
                                    {
                                        var userId = requestJson.GetProperty("user_id").GetInt64();
                                        var group = requestJson.GetProperty("group_id").GetInt64();
                                        var messageId = requestJson.GetProperty("message_id").GetInt32();
                                        if (!_gpt.GetUserCompleted(userId))
                                        {
                                            var body = GetSendGroupMessageBody(group, messageId, "您的上条请求尚未完成，请稍后");
                                            _ = webSocket.SendAsync(new ArraySegment<byte>(body, 0, body.Length), WebSocketMessageType.Text, result.EndOfMessage, CancellationToken.None);
                                            continue;
                                        }
                                        _logger.LogInformation("收到ChatGPT请求：{message}", match.Groups[1].Value);
                                        _ = _gpt.RequestConversation(userId, match.Groups[1].Value, async (str, exp) =>
                                        {
                                            _logger.LogInformation("回复：{message}, {str}", rawMessage, str);
                                            var body = GetSendGroupMessageBody(group, messageId, exp == null ? str : exp.Message);
                                            _ = webSocket.SendAsync(new ArraySegment<byte>(body, 0, body.Length), WebSocketMessageType.Text, result.EndOfMessage, CancellationToken.None);
                                        }, CancellationToken.None);
                                    }
                                }
                            }
                            break;
                        default:
                            _logger.LogWarning("收到了未知事件：{Type}", postType);
                            break;
                    }
                    /*
                    _ = _gpt.RequestConversation(114514, request, async (str, exp) =>
                    {
                        var msBuffer = Encoding.UTF8.GetBytes(str);
                        await webSocket.SendAsync(new ArraySegment<byte>(msBuffer, 0, msBuffer.Length), WebSocketMessageType.Text, result.EndOfMessage, CancellationToken.None);
                    }, CancellationToken.None);*/
                }
                else
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "消息类型不可用", CancellationToken.None);
                    _logger.LogInformation("与客户端{address}的连接中断，客户端传递了非法的消息。", context.Request.RemoteEndPoint);
                }
            }
        }

        private byte[] GetSendGroupMessageBody(long groupId, long replyId, string message)
        {
            var json = new
            {
                action = "send_group_msg",
                echo = Guid.NewGuid().ToString("D"),
                @params = new
                {
                    group_id = groupId,
                    message = $"[CQ:reply,id={replyId}]{message}"
                }
            };
            var c = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(json));
            return c;
        }
    }
}
