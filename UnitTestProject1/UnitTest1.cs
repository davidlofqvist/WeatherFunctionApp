using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using System;
using Microsoft.Extensions.Logging;
using WeatherFunctionApp;
using Microsoft.Extensions.Configuration;
using System.Threading.Tasks;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using System.Net;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace UnitTestProject1
{
    /// <summary>
    /// Unit tests for WeatherApp.
    /// </summary>
    [TestClass]
    public class UnitTest1
    {
        private Mock<ILoggerFactory> _loggerFactoryMock;
        private Mock<ILogger<WeatherApp>> _loggerMock;
        private Mock<IConfiguration> _configurationMock;
        private WeatherApp _weatherApp;
        private string _logEntryID;

        /// <summary>
        /// Initializes the test setup.        
        [TestInitialize]
        public void Setup()
        {
            _loggerFactoryMock = new Mock<ILoggerFactory>();
            _loggerMock = new Mock<ILogger<WeatherApp>>();
            _configurationMock = new Mock<IConfiguration>();

            _loggerFactoryMock.Setup(lf => lf.CreateLogger(It.IsAny<string>())).Returns(_loggerMock.Object);
            _configurationMock.SetupGet(c => c["AzureWebJobsStorage"]).Returns("UseDevelopmentStorage=true");

            _weatherApp = new WeatherApp(_configurationMock.Object, _loggerFactoryMock.Object);

            // prepare the data and save the log entry id
            var timerInfo = new MyInfo();
            var result = _weatherApp.FetchWeatherDataAndStoreData(timerInfo);
            result.Wait();
            _logEntryID = result.Result.ToString();
        }

        /// <summary>
        /// Tests FetchWeatherDataAndStoreData method to ensure it returns weather data.  
        [TestMethod]
        public async Task FetchWeatherDataAndStoreData_ShouldReturnWeatherData()
        {
            var timerInfo = new MyInfo();
            var result = await _weatherApp.FetchWeatherDataAndStoreData(timerInfo);

            Assert.IsNotNull(result);
        }

        /// <summary>
        /// Tests GetWeatherLogs method to ensure it returns logs.                
        [TestMethod]
        public async Task GetWeatherLogs_ShouldReturnLogs()
        {
            var functionContext = new Mock<FunctionContext>();
            var request = new FakeHttpRequestData(functionContext.Object,
                new Uri($"http://localhost:7292/api/GetWeatherLogs?from=2025-03-01T00:00:00Z&to={DateTime.Now}"));

            var result = await _weatherApp.GetWeatherLogs(request, request.FunctionContext);
            result.Body.Position = 0;

            using (var reader = new StreamReader(result.Body))
            {
                var responseBody = await reader.ReadToEndAsync();
                Assert.IsTrue(responseBody.Contains("WeatherInfo"));
            }

            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        }

        /// <summary>
        /// Tests GetWeatherPayload method to ensure it returns payload.
        [TestMethod]
        public async Task GetWeatherPayload_ShouldReturnPayload()
        {
            var functionContext = new Mock<FunctionContext>();
            var request = new FakeHttpRequestData(functionContext.Object,
                new Uri($"http://http://localhost:7292/api/GetWeatherPayload?logEntryid={_logEntryID}"));
            var result = await _weatherApp.GetWeatherPayload(request, request.FunctionContext);
            result.Body.Position = 0;

            using (var reader = new StreamReader(result.Body))
            {
                var responseBody = await reader.ReadToEndAsync();
                Assert.IsTrue(responseBody.Contains("coord"));
            }
            Assert.AreEqual(HttpStatusCode.OK, result.StatusCode);
        }

        /// <summary>
        /// Fake HTTP request data for testing purposes.
        /// </summary>
        [ExcludeFromCodeCoverage]
        public class FakeHttpRequestData : HttpRequestData
        {
            public FakeHttpRequestData(FunctionContext functionContext, Uri url, Stream body = null) : base(functionContext)
            {
                Url = url;
                Body = body ?? new MemoryStream();
            }
            public override Stream Body { get; } = new MemoryStream();
            public override HttpHeadersCollection Headers { get; } = new HttpHeadersCollection();
            public override IReadOnlyCollection<IHttpCookie> Cookies { get; }
            public override Uri Url { get; }
            public override IEnumerable<ClaimsIdentity> Identities { get; }
            public override string Method { get; }
            public override HttpResponseData CreateResponse()
            {
                return new FakeHttpResponseData(FunctionContext);
            }
        }

        /// <summary>
        /// Fake HTTP response data for testing purposes.
        /// </summary>
        [ExcludeFromCodeCoverage]
        public class FakeHttpResponseData : HttpResponseData
        {
            public FakeHttpResponseData(FunctionContext functionContext) : base(functionContext)
            {
            }

            public override HttpStatusCode StatusCode { get; set; }
            public override HttpHeadersCollection Headers { get; set; } = new HttpHeadersCollection();
            public override Stream Body { get; set; } = new MemoryStream();
            public override HttpCookies Cookies { get; }
        }
    }
}
