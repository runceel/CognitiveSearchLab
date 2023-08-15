using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text.Json;

// モデルのベクトルの次元数
const int ModelDimensions = 1536;
// インデックス名
const string IndexName = "test-index";

// 設定の読み込み
var c = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();
var openAIOptions = c.GetSection(OpenAIOptions.KeyName).Get<OpenAIOptions>() ?? throw new InvalidOperationException();
var cognitiveSearchOptions = c.GetSection(CognitiveSearchOptions.KeyName).Get<CognitiveSearchOptions>() ?? throw new InvalidOperationException();

// AOAI と Cognitive Search への接続用クライアントの作成
var openAIClient = new OpenAIClient(
    new Uri(openAIOptions.Endpoint),
    new AzureKeyCredential(openAIOptions.ApiKey));
var cognitiveSearchClient = new SearchIndexClient(
    new Uri(cognitiveSearchOptions.Endpoint),
    new AzureKeyCredential(cognitiveSearchOptions.ApiKey));

// インデックスを作成する
await CreateIndexAsync(cognitiveSearchClient);
// データを登録する
await InsertDataAsync(openAIClient, openAIOptions.ModelName, cognitiveSearchClient);
// 検索をする
await ReadWordAndSearchAsync(openAIClient, openAIOptions.ModelName, cognitiveSearchOptions);

// インデックスを作成する
async ValueTask CreateIndexAsync(SearchIndexClient client)
{
    // ベクトル検索の設定名
    const string VectorSearchConfigName = "vector-config-for-test";

    var searchIndex = new SearchIndex(IndexName)
    {
        VectorSearch = new()
        {
            AlgorithmConfigurations =
            {
                new HnswVectorSearchAlgorithmConfiguration(VectorSearchConfigName),
            }
        },
        Fields =
        {
            // 一意識別のための列
            new SimpleField("id", SearchFieldDataType.String) { IsKey = true },
            // サービス名と概要
            new SimpleField("serviceName", SearchFieldDataType.String),
            // SearchableField が 1 つはないと検索できないので使わないけど SearchableField にしておく
            new SearchableField("description") { IsFilterable = true }, 
            // 概要のベクトルデータ
            new SearchField("descriptionVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
            {
                IsSearchable = true,
                VectorSearchDimensions = ModelDimensions,
                VectorSearchConfiguration = VectorSearchConfigName
            }
        }
    };

    // 作成または更新をする
    await client.CreateOrUpdateIndexAsync(searchIndex);
}

// ベクトル化
async ValueTask<float[]> GenerateEmbeddingsAsync(OpenAIClient openAIClient, string modelName, string text)
{
    var result = await openAIClient.GetEmbeddingsAsync(modelName, new EmbeddingsOptions(text));
    return result.Value.Data[0].Embedding.ToArray();
}

// データを作成したインデックスに追加する
async ValueTask InsertDataAsync(OpenAIClient openAIClient, string modelName, SearchIndexClient cognitiveSearchClient)
{
    static async ValueTask<ServiceDescription[]?> readAsync()
    {
        using var file = File.OpenRead("services.json");
        return await JsonSerializer.DeserializeAsync<ServiceDescription[]>(file, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    var serviceDescriptions = await readAsync() ?? throw new InvalidOperationException();
    List<SearchDocument> documents = new();
    foreach (var serviceDescription in serviceDescriptions)
    {
        var embeddings = await GenerateEmbeddingsAsync(openAIClient, modelName, serviceDescription.Description);
        documents.Add(new SearchDocument
        {
            ["id"] = serviceDescription.ServiceName.Replace(" ", ""),
            ["serviceName"] = serviceDescription.ServiceName,
            ["description"] = serviceDescription.Description,
            ["descriptionVector"] = embeddings,
        });
    }

    var searchClient = cognitiveSearchClient.GetSearchClient(IndexName);
    await searchClient.MergeOrUploadDocumentsAsync(documents);
}

// ベクトル検索
async ValueTask ReadWordAndSearchAsync(OpenAIClient openAIClient, string modelName, CognitiveSearchOptions options)
{
    // SearchIndexClient から SearchClient を作成する以外にも、直接 SearchClient を作る方法もあるのでそっちで作る
    // 普通は検索アプリは検索だけすることが多いと思うので、その場合はこのように SearchClient を作ることになる。
    var searchClient = new SearchClient(
        new Uri(options.Endpoint), IndexName, new AzureKeyCredential(options.ApiKey));

    while (true)
    {
        Console.Write("やりたいことを入力してください: ");
        var input = Console.ReadLine();
        if (string.IsNullOrEmpty(input) || input == "exit")
        {
            break;
        }

        var embeddings = await GenerateEmbeddingsAsync(openAIClient, modelName, input);

        var searchOptions = new SearchOptions
        {
            Vectors =
            {
                new SearchQueryVector
                {
                    KNearestNeighborsCount = 3, // 3 件返す
                    Fields = { "descriptionVector" },
                    Value = embeddings
                }
            },
            Size = 3, // 3 件返す
            Select = { "serviceName", "description" },
        };

        var response = await searchClient.SearchAsync<SearchDocument>(null, searchOptions);
        Console.WriteLine("やりたいことを実現できる可能性のあるサービスは以下のものになります。");
        await foreach (var result in response.Value.GetResultsAsync())
        {
            Console.WriteLine($"サービス名: {result.Document["serviceName"]}, スコア: {result.Score}");
            Console.WriteLine($"概要: {result.Document["description"]}");
            Console.WriteLine();
        }
    }
}


// 設定を読み込むためのクラス
class OpenAIOptions
{
    public const string KeyName = "OpenAI";
    public required string Endpoint { get; set; }
    public required string ApiKey { get; set; }
    public required string ModelName { get; set; }
}

class CognitiveSearchOptions
{
    public const string KeyName = "CognitiveSearch";

    public required string Endpoint { get; set; }
    public required string ApiKey { get; set; }
}

class ServiceDescription
{
    public string ServiceName { get; set; } = "";
    public string Description { get; set; } = "";
}
