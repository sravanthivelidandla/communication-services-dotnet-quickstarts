using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.CognitiveServices.Speech.Transcription;
using Microsoft.Extensions.Logging;


namespace CallAutomationOpenAI
{
    /// <summary>
    /// Service responsible for analyzing call transcripts and providing analytics
    /// </summary>
    public class CallAnalyticsService
    {
        private readonly IConfiguration _configuration;

        public CallAnalyticsService(IConfiguration configuration)
        {
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public async Task<CallAnalytics> AnalyzeTranscriptWithChatGPT( string transcript)
        { // Get OpenAI configuration from app settings
            var openAiKey = _configuration.GetValue<string>("AzureOpenAIServiceKey");
            var openAiUri = _configuration.GetValue<string>("AzureOpenAIServiceEndpoint");
            var openAiModelName = _configuration.GetValue<string>("AzureOpenAIDeploymentModelName");
            string apiVersion = "2024-02-15-preview";


            Console.WriteLine($"Transcript : --- {transcript}");

            string prompt = @$"
                Please analyze the following customer service transcript and return the result strictly in JSON format with the following fields:
                - summary
                - intent
                - sentiment (positive, neutral, or negative
                - suggestedActions

                Transcript:
                {transcript}

                Return format:
                {{
                  ""summary"": ""..."",
                  ""intent"": ""..."",
                  ""sentiment"": ""..."",
                  ""suggestedActions"": ""...""
                }}";

            var requestBody = new
            {
                messages = new[]
                {
                new { role = "system", content = "You are a helpful assistant that summarizes customer service calls, identifies customer intent, and analyzes sentiment. Also highlight suggested actions" },
                new { role = "user", content = prompt }
            },
                temperature = 0.3
            };

            var json = JsonSerializer.Serialize(requestBody);
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("api-key", openAiKey);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(
                "https://insravan-aoai.openai.azure.com/openai/deployments/gpt-4.1/chat/completions?api-version=2025-01-01-preview", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                throw new Exception(error);
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var result = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            var analytics = JsonSerializer.Deserialize<CallAnalytics>(result);//ParseOpenAIResponse(result);

            Console.WriteLine($"Analytics Result: {JsonSerializer.Serialize(analytics)}");

            return analytics;
        }

        public static CallAnalytics ParseOpenAIResponse(string response)
        {
            var analytics = new CallAnalytics();

            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.StartsWith("1.") || line.StartsWith("Summary:", StringComparison.OrdinalIgnoreCase))
                {
                    analytics.callSummary = ExtractValue(line);
                }
                else if (line.StartsWith("2.") || line.StartsWith("Intent:", StringComparison.OrdinalIgnoreCase))
                {
                    analytics.callIntent = ExtractValue(line);
                }
                else if (line.StartsWith("3.") || line.StartsWith("Sentiment:", StringComparison.OrdinalIgnoreCase))
                {
                    analytics.callSentiment = ExtractValue(line);
                }
                else if (line.StartsWith("4.") || line.StartsWith("SuggestedActions:", StringComparison.OrdinalIgnoreCase))
                {
                    analytics.suggestedActions = ExtractValue(line);
                }
            }

            return analytics;
        }

        private static string ExtractValue(string line)
        {
            var colonIndex = line.IndexOf(':');
            return colonIndex > -1 ? line[(colonIndex + 1)..].Trim() : line.Trim();
        }

       
    }
}