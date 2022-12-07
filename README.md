# Himari.ChatGPT
调用OpenAI ChatGPT WebAPI进行聊天的OneBot应用，是Himari Bot的一部分。

## 环境要求
[.NET 6](https://dotnet.microsoft.com/en-us/download/dotnet/6.0)及以上  

## 启动方式
在Windows下，您可以直接或使用cmd或Powershell启动编译后的exe。  
在Linux下，您可以使用```dotnet Himari.ChatGPT.dll```指令启动。

## 配置文件
您需要在启动目录下建立名为```himari.chatgpt.json```的配置文件。
```json
{
	"OAIUsername": "",
	"OAIPassword": "",
	"AccessToken": "",
	"BindIp": "127.0.0.1",
	"BindPort": 8085
}
```

```OAIUsername```为您OpenAI账号的用户名。  
```OAIPassword```为您OpenAI账号的密码。  
```AccessToken```默认为空，使用OneBot标准的鉴权，如go-cqhttp中的AccessToken。  
```BindIp```和```BindPort```为WebSocket服务绑定的IP和端口。  

## 指令
```/chat <对话内容>```  
例如：使用```/chat 你好```   
返回内容为：```你好，我是 Assistant，一个由 OpenAI 训练的语言模型。我能回答一些问题，帮助你了解更多信息。有什么可以为您效劳的吗？```
