using Microsoft.Extensions.Configuration;
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

// One gateway per provider mode: AzureFoundry and OpenAI share the Responses API wire format
// (ResponsesApiGateway), Anthropic speaks the Messages API with the MCP connector. The provider
// is read from configuration here (not via IOptions) because the choice decides which type to
// construct; each gateway still receives its settings through the validated options.
var provider = builder.Configuration.GetSection("SlackBot").GetValue<AssistantProvider?>("Provider")
    ?? AssistantProvider.AzureFoundry;
if (provider == AssistantProvider.Anthropic)
{
    builder.Services.AddSingleton<IAssistantGateway, AnthropicGateway>();
}
else
{
    builder.Services.AddSingleton<IAssistantGateway, ResponsesApiGateway>();
}
builder.Services.AddHostedService<SlackSocketWorker>();

await builder.Build().RunAsync().ConfigureAwait(false);
