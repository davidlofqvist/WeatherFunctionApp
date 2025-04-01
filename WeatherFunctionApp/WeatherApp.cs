using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System;
using Azure.Data.Tables;
using Azure;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Web;
using System.Linq;

namespace WeatherFunctionApp
{
    /// <summary>
    /// Azure Function App to fetch and store weather data.
    /// </summary>
    public class WeatherApp
    {
        private readonly ILogger _logger;
        private readonly IConfiguration _configuration;
        private static readonly HttpClient httpClient = new HttpClient();
        private const string apiKey = "70b19a8d376e71f0a11d034682a6c8b9";
        private const string tableName = "WeatherInfoTable";
        private readonly string storageConnectionString;
        private readonly TableClient _tableClient;
        private readonly BlobServiceClient _blobServiceClient;
        private readonly BlobContainerClient _containerClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="WeatherApp"/> class.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        /// <param name="loggerFactory">The logger factory.</param>
        public WeatherApp(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            _configuration = configuration;
            _logger = loggerFactory.CreateLogger<WeatherApp>();
            storageConnectionString = _configuration["AzureWebJobsStorage"];
            _tableClient = new TableClient(storageConnectionString, tableName);
            _blobServiceClient = new BlobServiceClient(storageConnectionString);
            _containerClient = _blobServiceClient.GetBlobContainerClient("weatherdata");
            _containerClient.CreateIfNotExists();
        }

        /// <summary>
        /// HTTP trigger function to start the weather app.
        /// </summary>
        /// <param name="req">The HTTP request.</param>
        /// <param name="executionContext">The function execution context.</param>
        /// <returns>The HTTP response.</returns>
        [Function("WeatherApp")]
        public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req, FunctionContext executionContext)
        {
            _logger.LogInformation("C# HTTP trigger function processed a request.");

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "text/plain; charset=utf-8");

            await response.WriteStringAsync("Starting weatherapp");

            // Get the weather data, and store it in the table and blob storage
            await FetchWeatherDataAndStoreData(new MyInfo());

            return response;
        }

        /// <summary>
        /// Timer trigger function to fetch weather data and store it in table and blob storage.
        /// </summary>
        /// <param name="timer">The timer info.</param>
        /// <returns>The weather data or error message.</returns>
        [Function("FetchWeatherDataAndStoreData")]
        [TableOutput("WeatherInfoTable", Connection = "AzureWebJobsStorage")]
        public async Task<string> FetchWeatherDataAndStoreData([TimerTrigger("0 */1 * * * *", RunOnStartup = true)] MyInfo timer)
        {
            _logger.LogInformation("Fetching weather data...");

            var response = await httpClient.GetAsync($"https://api.openweathermap.org/data/2.5/weather?q=London&appid={apiKey}");

            var infoTable = new WeatherInfoTable()
            {
                PartitionKey = "WeatherInfo",
                RowKey = Guid.NewGuid().ToString(),
                Timestamp = DateTimeOffset.UtcNow,
                FetchStatus = response.IsSuccessStatusCode,
                ETag = new ETag()
            };

            _tableClient.AddEntity(infoTable);

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Weather data: {data}");

                // Store the data in the blob storage
                var blobClient = _containerClient.GetBlobClient(infoTable.RowKey);
                using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(data)))
                {
                    await blobClient.UploadAsync(stream, true);
                }
                return data;
            }
            else
            {
                var errorMessage = $"Failed to fetch weather data. FetchStatus code: {response.StatusCode}";
                _logger.LogError(errorMessage);
                return errorMessage;
            }
        }

        /// <summary>
        /// HTTP trigger function to get weather logs.
        /// </summary>
        /// <param name="req">The HTTP request.</param>
        /// <param name="executionContext">The function execution context.</param>
        /// <returns>The HTTP response with weather logs.</returns>
        [Function("GetWeatherLogs")]
        public async Task<HttpResponseData> GetWeatherLogs([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req, FunctionContext executionContext)
        {
            _logger.LogInformation("GetWeatherLogs function processed a request to get logs.");

            var queryParams = HttpUtility.ParseQueryString(req.Url.Query);
            var from = DateTimeOffset.Parse(queryParams.Get("from"));
            var to = DateTimeOffset.Parse(queryParams.Get("to"));
            var response = req.CreateResponse(HttpStatusCode.NoContent);

            var logs = _tableClient.Query<WeatherInfoTable>(log => log.Timestamp >= from && log.Timestamp <= to)
                                   .OrderByDescending(log => log.Timestamp)
                                   .ToList();

            if (logs.Any())
            {
                response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                var formattedLogs = logs.Select(log => new
                {
                    log.PartitionKey,
                    log.RowKey,
                    Timestamp = log.Timestamp?.ToString("o"),
                    log.FetchStatus
                });

                var logsWithLineBreaks = string.Join(Environment.NewLine,
                            formattedLogs.OrderByDescending(log => log.Timestamp)
                                         .Select(log => System.Text.Json.JsonSerializer.Serialize(log)));

                await response.WriteStringAsync(logsWithLineBreaks);
            }

            return response;
        }

        /// <summary>
        /// HTTP trigger function to get weather payload.
        /// </summary>
        /// <param name="req">The HTTP request.</param>
        /// <param name="executionContext">The function execution context.</param>
        /// <returns>The HTTP response with weather payload.</returns>
        [Function("GetWeatherPayload")]
        public async Task<HttpResponseData> GetWeatherPayload([HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req, FunctionContext executionContext)
        {
            _logger.LogInformation("GetWeatherPayload function processed a request to get payload.");

            var queryParams = HttpUtility.ParseQueryString(req.Url.Query);
            var logEntryId = queryParams.Get("logEntryId");

            if (string.IsNullOrEmpty(logEntryId))
            {
                var badRequestResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequestResponse.WriteStringAsync("logEntryId is required.");
                return badRequestResponse;
            }

            var blobClient = _containerClient.GetBlobClient(logEntryId);

            if (await blobClient.ExistsAsync())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json; charset=utf-8");

                var blobDownloadInfo = await blobClient.DownloadAsync();
                using (var streamReader = new StreamReader(blobDownloadInfo.Value.Content))
                {
                    var content = await streamReader.ReadToEndAsync();
                    await response.WriteStringAsync(content);
                }

                return response;
            }
            else
            {
                var notFoundResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await notFoundResponse.WriteStringAsync("Log entry not found.");
                return notFoundResponse;
            }
        }
    }
    /// <summary>
    /// Represents a weather information table entity.
    /// </summary>
    public class WeatherInfoTable : ITableEntity
    {
        /// <summary>
        /// Gets or sets the partition key.
        /// </summary>
        public string PartitionKey { get; set; }

        /// <summary>
        /// Gets or sets the row key.
        /// </summary>
        public string RowKey { get; set; }

        /// <summary>
        /// Gets or sets the timestamp.
        /// </summary>
        public DateTimeOffset? Timestamp { get; set; }

        /// <summary>
        /// Gets or sets the fetch status.
        /// </summary>
        public bool FetchStatus { get; set; }

        /// <summary>
        /// Gets or sets the ETag.
        /// </summary>
        public ETag ETag { get; set; }
    }

    /// <summary>
    /// Represents the timer trigger information.
    /// </summary>
    public class MyInfo
    {
        /// <summary>
        /// Gets or sets the schedule status.
        /// </summary>
        public ScheduleStatus ScheduleStatus { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the timer is past due.
        /// </summary>
        public bool IsPastDue { get; set; }
    }

    /// <summary>
    /// Represents the schedule status.
    /// </summary>
    public class ScheduleStatus
    {
        /// <summary>
        /// Gets or sets the last run time.
        /// </summary>
        public DateTime Last { get; set; }

        /// <summary>
        /// Gets or sets the next run time.
        /// </summary>
        public DateTime Next { get; set; }

        /// <summary>
        /// Gets or sets the last updated time.
        /// </summary>
        public DateTime LastUpdated { get; set; }
    }
}
