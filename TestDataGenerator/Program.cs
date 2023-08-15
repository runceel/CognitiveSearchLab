using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

var c = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var options = c.GetSection("OpenAI")
    .Get<OpenAIOptions>()
    ?? throw new InvalidOperationException();

var client = new OpenAIClient(
    new Uri(options.Endpoint),
    new AzureKeyCredential(options.ApiKey));

var serviceNames = new[]
{
    "CosmosDB", "Azure Functions", "App Service", "SQL Database",
    "Azure Kubernetes Service", "Azure Container Instances", "Azure Container Registry",
    "Azure DevOps", "Azure Pipelines", "Azure Boards", "Azure Repos", "Azure Artifacts",
    "Azure Test Plans", "Azure Monitor", "Azure Application Insights", "Azure Log Analytics",
    "Azure Resource Manager", "Azure Resource Graph", "Azure Policy", "Azure Blueprints", "Azure Cost Management",
    "Azure Advisor", "Azure Security Center", "Azure Sentinel", "Azure Defender", "Azure Key Vault",
};

const string systemPrompt = """
    あなたはMicrosoft Azureの専門家として、ユーザーが指定したAzureサービスの概要を100文字以上、500文字以内で解説してください。
    """;

var results = new List<ServiceDescription>();
foreach (var serviceName in serviceNames)
{
    Console.WriteLine($"サービス名: {serviceName}");
    var chatCompletionsOptions = new ChatCompletionsOptions
    {
        ChoiceCount = 1,
        MaxTokens = 1000,
        Temperature = 0.0f,
        Messages =
        {
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, serviceName),
        },
    };

    var response = await client.GetChatCompletionsAsync(
        options.ModelName,
        chatCompletionsOptions);

    var choice = response.Value.Choices[0];
    if (choice.FinishReason != CompletionsFinishReason.Stopped)
    {
        Console.WriteLine($"何かエラー: {choice.FinishReason}");
        return;
    }

    results.Add(new()
    {
        ServiceName = serviceName,
        Description = choice.Message.Content,
    });
}

if (File.Exists("services.json"))
{
    File.Delete("services.json");
}

using var file = File.OpenWrite("services.json");
await JsonSerializer.SerializeAsync<List<ServiceDescription>>(
    file,
    results,
    new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    });
await file.FlushAsync();

class OpenAIOptions
{
    public required string Endpoint { get; set; }
    public required string ApiKey { get; set; }
    public required string ModelName { get; set; }
}

class ServiceDescription
{
    public string ServiceName { get; set; } = "";
    public string Description { get; set; } = "";
}