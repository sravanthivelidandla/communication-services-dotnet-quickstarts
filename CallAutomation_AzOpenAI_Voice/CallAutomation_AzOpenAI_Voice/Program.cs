using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Azure;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.Net.WebSockets;

var builder = WebApplication.CreateBuilder(args);
AcsMediaStreamingHandler mediaService = null;

//Get ACS Connection String from appsettings.json
var acsConnectionString = builder.Configuration.GetValue<string>("AcsConnectionString");
ArgumentNullException.ThrowIfNullOrEmpty(acsConnectionString);

//Call Automation Client
var client = new CallAutomationClient(acsConnectionString);
var app = builder.Build();

var appBaseUrl = builder.Configuration.GetValue<string>("AppServiceUri")?.TrimEnd('/');
string callConnectionId = null;

if (string.IsNullOrEmpty(appBaseUrl))
{
    var websiteHostName = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
    Console.WriteLine($"websiteHostName :{websiteHostName}");
    appBaseUrl = $"https://{websiteHostName}";
    Console.WriteLine($"appBaseUrl :{appBaseUrl}");
}

app.MapGet("/", () => "Hello ACS CallAutomation!");

app.MapPost("/api/incomingCall", async (
    [FromBody] EventGridEvent[] eventGridEvents,
    ILogger<Program> logger) =>
{
    foreach (var eventGridEvent in eventGridEvents)
    {
        var startime = DateTime.Now;
        Console.WriteLine($"Incoming Call event received. {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        logger.LogInformation("Incoming Call event received at {Timestamp}", DateTime.Now);

        Task.Run(async () =>
        {
            var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
            var callerId = Helper.GetCallerId(jsonObject);
            var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
            //var incomingCallContext1 = Helper.DecompressGzipString(jsonObject["incomingCallContext"]);
            var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
            logger.LogInformation($"Callback Url: {callbackUri}");
            var websocketUri = appBaseUrl.Replace("https", "wss") + "/ws";
            logger.LogInformation($"WebSocket Url: {websocketUri}");

            var mediaStreamingOptions = new MediaStreamingOptions(
                    new Uri(websocketUri),
                    MediaStreamingContent.Audio,
                    MediaStreamingAudioChannel.Mixed,
                    startMediaStreaming: true
                    )
            {
                EnableBidirectional = true,
                AudioFormat = AudioFormat.Pcm24KMono
            };

            var options = new AnswerCallOptions(incomingCallContext, callbackUri)
            {
               MediaStreamingOptions = mediaStreamingOptions,
            };

            AnswerCallResult answerCallResult = await client.AnswerCallAsync(options);
            callConnectionId = answerCallResult.CallConnection.CallConnectionId;            

            var endtime = DateTime.Now;
            Console.WriteLine($"Call answered at : {DateTime.Now:yyyy - MM - dd HH: mm: ss.fff} ");
            logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");
            var duration = (endtime - startime).TotalMilliseconds;
            Console.WriteLine($"Call answered in: {duration} ms");
            logger.LogInformation("Call answered in {Duration} ms", duration);
        });
    }

    Console.WriteLine($"Return incoming call response. {DateTime.Now:yyyy - MM - dd HH: mm: ss.fff}");
    logger.LogInformation("Returning incoming call response at {Timestamp}", DateTime.Now);
    return Results.Ok();
});

app.MapGet("/health", () => "Hello ACS CallAutmation");

app.MapPost("/api/transferCall", async (
    [FromBody] TransferRequest transferRequest,
    ILogger<Program> logger) =>
{
    try
    {
        //var callConnectionId = transferRequest.CallConnectionId;
        //var targetPhoneNumber = transferRequest.TargetPhoneNumber;

        // var transferTo = new Azure.Communication.PhoneNumberIdentifier("+14257270513");
        var transferTo = new Azure.Communication.MicrosoftTeamsUserIdentifier("6e8aae3b-c44c-44ab-8569-f9b5c028fa17");
        var transferOptions = new TransferToParticipantOptions(transferTo);
        transferOptions.CustomCallingContext.VoipHeaders.Add("prescriptionId", "123456");
        
        //transferOptions.CustomCallingContext.AddVoip("prescriptionId", "123456");
        //transferOptions.CustomCallingContext.AddSipX("PhoneNumber", "1234567");
        //transferOptions.CustomCallingContext.AddSipUui("drugname=Advil");

        var callConnection = client.GetCallConnection(callConnectionId);
        var transferResult = await callConnection.TransferCallToParticipantAsync(transferOptions);
        logger.LogInformation($"Call transferred to abc for connection id: {callConnectionId}");

        return Results.Ok(new { Message = "Call transferred successfully" });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error transferring call: {ex.Message}");
        //logger.LogError(ex, "Error transferring call");
        return Results.Problem("Error transferring call");
    }
});

app.MapPost("/api/addParticipant", async (
    [FromBody] AddParticipantRequest addParticipantRequest,
    ILogger<Program> logger) =>
{
    try
    {
        var callConnection = client.GetCallConnection(callConnectionId);
        //var callinviteToAdd = new CallInvite(new Azure.Communication.PhoneNumberIdentifier("+14257270513"), new Azure.Communication.PhoneNumberIdentifier("+18882806852"));
        //var callInviteToAdd = new CallInvite(new Azure.Communication.MicrosoftTeamsUserIdentifier("6e8aae3b-c44c-44ab-8569-f9b5c028fa17"));

        //This is the working CQ one.
        var callinviteToAdd = new CallInvite(new Azure.Communication.MicrosoftTeamsAppIdentifier("5d1d11ac-efac-408c-9a2b-3292993e89f1"));
        callinviteToAdd.CustomCallingContext.AddVoip("prescriptionId", "123456");
      
        AddParticipantResult addParticipantResult = await callConnection.AddParticipantAsync(new AddParticipantOptions(callinviteToAdd));

        // giving 30 seconds timeout for call reciever to answer
        CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        CancellationToken token = cts.Token;

        //logger.LogInformation("Waiting for participant to answer call, timeout: 30 seconds");
        // this will wait until CreateCall is completed or Timesout!
        AddParticipantEventResult eventResult = addParticipantResult.WaitForEventProcessor(token);

        // Once this is recieved, you know the call is now connected.
        if(eventResult.IsSuccess)
        {
            var addParticipantCompletedEvent = eventResult.SuccessResult;
            Console.WriteLine($"Call connected with Teams user with id: {addParticipantCompletedEvent.Participant}");
           // logger.LogInformation($"Call connected with Teams user with id: {addParticipantCompletedEvent.Participant}");

            //hangup the call as the aprticipant is already added. We wanted IVR to drop from the call.
            await callConnection.HangUpAsync(false);
            //logger.LogInformation("IVR disconnected from call after participant added");
        }
        else
        {
            Console.WriteLine($"Error adding participant: {eventResult.FailureResult.ResultInformation.Message}");
            //logger.LogError($"Error adding participant: {eventResult.FailureResult.ResultInformation.Message}");
        }
       // logger.LogInformation($"Participant added to call with connection id: {callConnectionId}");
    }
    catch (OperationCanceledException ex)
    {
        // Timeout exception happend!
        // Call likely was never answered.
        Console.WriteLine("Call timed out, likely never answered");
        logger.LogWarning(ex, "Call timed out after 30 seconds, likely never answered");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error adding participant: {ex.Message}");
        logger.LogError(ex, "Error adding participant");
        return Results.Problem("Error adding participant");
    }

    return Results.Ok(new { Message = "Participant added successfully" });
});


// api to handle call back events
app.MapPost("/api/callbacks/{contextId}", async (
    [FromBody] CloudEvent[] cloudEvents,
    [FromRoute] string contextId,
    [Required] string callerId,
    ILogger<Program> logger) =>
{
    foreach (var cloudEvent in cloudEvents)
    {
        CallAutomationEventBase @event = CallAutomationEventParser.Parse(cloudEvent);
        Console.WriteLine($"Event received: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");
      //  logger.LogInformation($"Event received: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");

        if (cloudEvent.Type == "Microsoft.Communication.CallDisconnected")
        {
            Console.WriteLine("Call disconnected event received, hanging up");
        //    logger.LogInformation("Call disconnected event received, hanging up");
            var callConnection = client.GetCallConnection(callConnectionId);
            await callConnection.HangUpAsync(true);
        }
    }

    return Results.Ok();
});

app.UseWebSockets();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/ws")
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            try
            {
                Console.WriteLine($"web socket received on. {DateTime.Now:yyyy - MM - dd HH: mm: ss.fff} ");
                //app.Logger.LogInformation("WebSocket connection request received at {Timestamp}", DateTime.Now);
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                
                // Get logger from the service provider
                var logger = context.RequestServices.GetRequiredService<ILogger<AcsMediaStreamingHandler>>();
                
                // Pass logger to the handler
                mediaService = new AcsMediaStreamingHandler(webSocket, builder.Configuration, client, callConnectionId);
                
                // Set the single WebSocket connection
                await mediaService.ProcessWebSocketAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception received {ex}");
            }
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }
    else
    {
        await next(context);
    }
});

app.Run();