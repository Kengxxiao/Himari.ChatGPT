using Himari.ChatGPT;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;

IConfiguration config = new ConfigurationBuilder().AddJsonFile("himari.chatgpt.json").AddEnvironmentVariables().Build();
IHost host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddSingleton(config);
        services.AddHttpClient("ChatGPT").ConfigurePrimaryHttpMessageHandler(() =>
        {
            HttpClientHandler handler = new()
            {
                AllowAutoRedirect = false,
                UseDefaultCredentials = true
            };
            return handler;
        });
        services.AddSingleton<ChatGPTClient>();
        services.AddHostedService<OnebotWebsocket>();
    }).Build();
await host.RunAsync();