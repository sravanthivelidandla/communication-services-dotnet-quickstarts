using System.Net.WebSockets;
using CallAutomationOpenAI;
using Azure.Communication.CallAutomation;
using System.Text;
using Microsoft.Extensions.Logging;
#pragma warning disable OPENAI002

public class AcsMediaStreamingHandler
{
    private WebSocket m_webSocket;
    private CancellationTokenSource m_cts;
    private MemoryStream m_buffer;
    private AzureOpenAIService m_aiServiceHandler;
    private IConfiguration m_configuration;
    private CallAutomationClient client;
    private string callConnectionId;
    private CustomCallingContext customContext; 
    private readonly ILogger<AcsMediaStreamingHandler> _logger;

    // Constructor to inject OpenAIClient and logger
    public AcsMediaStreamingHandler(WebSocket webSocket, IConfiguration configuration, CallAutomationClient client, string callConnectionId, CustomCallingContext customContext, ILogger<AcsMediaStreamingHandler> logger)
    {
        m_webSocket = webSocket;
        m_configuration = configuration;
        m_buffer = new MemoryStream();
        m_cts = new CancellationTokenSource();
        this.client = client;
        this.callConnectionId = callConnectionId;
        this.customContext = customContext;
        this._logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AcsMediaStreamingHandler>.Instance;
    }
      
    // Method to receive messages from WebSocket
    public async Task ProcessWebSocketAsync()
    {    
        if (m_webSocket == null)
        {
            //_logger?.LogWarning("WebSocket is null, cannot process WebSocket");
            return;
        }

        // Get the correct logger type from the service provider
        var openAIServiceLogger = Microsoft.Extensions.Logging.LoggerFactory
            .Create(builder => builder.AddConsole())
            .CreateLogger<AzureOpenAIService>();
        
        m_aiServiceHandler = new AzureOpenAIService(this, m_configuration, client, callConnectionId, customContext, openAIServiceLogger);
        
        try
        {
            _logger?.LogInformation("Starting conversation with call connection ID: {CallConnectionId}", callConnectionId);
            m_aiServiceHandler.StartConversation();
            await StartReceivingFromAcsMediaWebSocket();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception -> {ex}");
            _logger?.LogError(ex, "Exception occurred while processing WebSocket");
        }
        finally
        {
            //_logger?.LogInformation("Closing conversation session for call: {CallConnectionId}", callConnectionId);
            m_aiServiceHandler.Close();
            //if(m_webSocket != null )
            //    await CloseNormalWebSocketAsync();
            this.Close();
        }
    }

    public async Task SendMessageAsync(string message)
    {
        if (m_webSocket?.State == WebSocketState.Open)
        {
            byte[] jsonBytes = Encoding.UTF8.GetBytes(message);

            // Send the PCM audio chunk over WebSocket
            _logger?.LogDebug("Sending message over WebSocket, length: {Length} bytes", jsonBytes.Length);
            await m_webSocket.SendAsync(new ArraySegment<byte>(jsonBytes), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        else
        {
            _logger?.LogWarning("Cannot send message: WebSocket is not in Open state. Current state: {State}", m_webSocket?.State);
        }
    }

    public async Task CloseWebSocketAsync(WebSocketReceiveResult result)
    {
        //_logger?.LogInformation("Closing WebSocket with status: {CloseStatus}, description: {Description}", 
           // result.CloseStatus, result.CloseStatusDescription);
        await m_webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
    }

    public async Task CloseNormalWebSocketAsync()
    {
        //_logger?.LogInformation("Performing normal closure of WebSocket");
        await m_webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Stream completed", CancellationToken.None);
    }

    public void Close()
    {
        //_logger?.LogInformation("Closing AcsMediaStreamingHandler resources");
        m_cts.Cancel();
        m_cts.Dispose();
        m_buffer.Dispose();
    }

    private async Task WriteToAzOpenAIServiceInputStream(string data)
    {
        var input = StreamingData.Parse(data);
        if (input is AudioData audioData)
        {
            //_logger?.LogDebug("Sending audio data to AI service, size: {Size} bytes", audioData.Data.Length);
            using (var ms = new MemoryStream(audioData.Data))
            {
                await m_aiServiceHandler.SendAudioToExternalAI(ms);
            }
        }
    }

    // receive messages from WebSocket
    private async Task StartReceivingFromAcsMediaWebSocket()
    {
        if (m_webSocket == null)
        {
            //_logger?.LogWarning("WebSocket is null, cannot start receiving");
            return;
        }
        try
        {
            //_logger?.LogInformation("Starting to receive messages from ACS Media WebSocket");
            while (m_webSocket.State == WebSocketState.Open || m_webSocket.State == WebSocketState.Closed)
            {
                byte[] receiveBuffer = new byte[2048];
                WebSocketReceiveResult receiveResult = await m_webSocket.ReceiveAsync(new ArraySegment<byte>(receiveBuffer), m_cts.Token);

                if (receiveResult.MessageType != WebSocketMessageType.Close)
                {
                    //_logger?.LogDebug("Received WebSocket message, length: {Length} bytes", receiveResult.Count);
                    string data = Encoding.UTF8.GetString(receiveBuffer).TrimEnd('\0');
                    await WriteToAzOpenAIServiceInputStream(data);               
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Exception -> {ex}");
            _logger?.LogError(ex, "Exception occurred while receiving from WebSocket");
        }
    }
}