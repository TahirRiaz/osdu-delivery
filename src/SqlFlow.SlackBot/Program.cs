using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlFlow.Azure;
using SqlFlow.SlackBot;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOptions<SlackBotOptions>()
    .Bind(builder.Configuration.GetSection("SlackBot"))
    .Validate(options =>
    {
        // Validate() throws with the full list of missing settings, which surfaces verbatim at
        // startup; returning true here means "no additional predicate failures".
        options.Validate();
        return true;
    })
    .ValidateOnStart();

builder.Services.AddSingleton<IAzureCredentialFactory, AzureCredentialFactory>();
builder.Services.AddSingleton<FoundryAgentGateway>();
builder.Services.AddHostedService<SlackSocketWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
