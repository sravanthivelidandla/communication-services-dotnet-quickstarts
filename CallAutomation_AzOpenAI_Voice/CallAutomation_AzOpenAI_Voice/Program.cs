using Azure.Communication.CallAutomation;
using Azure.Messaging;
using Azure.Messaging.EventGrid;
using Azure.Messaging.EventGrid.SystemEvents;
using Microsoft.AspNetCore.Mvc;
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

        Task.Run(async () =>
        {
            //// Handle system events
            //if (eventGridEvent.TryGetSystemEventData(out object eventData))
            //{
            //    // Handle the subscription validation event.
            //    if (eventData is SubscriptionValidationEventData subscriptionValidationEventData)
            //    {
            //        var responseData = new SubscriptionValidationResponse
            //        {
            //            ValidationResponse = subscriptionValidationEventData.ValidationCode
            //        };
            //    }
            //}

            var jsonObject = Helper.GetJsonObject(eventGridEvent.Data);
            var callerId = Helper.GetCallerId(jsonObject);
            var incomingCallContext = Helper.GetIncomingCallContext(jsonObject);
            //var incomingCallContext1 = Helper.DecompressGzipString(jsonObject["incomingCallContext"]);
            var callbackUri = new Uri(new Uri(appBaseUrl), $"/api/callbacks/{Guid.NewGuid()}?callerId={callerId}");
            logger.LogInformation($"Callback Url: {callbackUri}");
            var websocketUri = appBaseUrl.Replace("https", "wss") + "/ws";
            logger.LogInformation($"WebSocket Url: {callbackUri}");

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
            logger.LogInformation($"Answered call for connection id: {answerCallResult.CallConnection.CallConnectionId}");
            var duration = (endtime - startime).TotalMilliseconds;
            Console.WriteLine($"Call answered in: {duration} ms");
        });
    }

    Console.WriteLine($"Return incoming call response. {DateTime.Now:yyyy - MM - dd HH: mm: ss.fff}");
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
        logger.LogError($"Error transferring call: {ex.Message}");
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


        // this will wait until CreateCall is completed or Timesout!
        AddParticipantEventResult eventResult = addParticipantResult.WaitForEventProcessor(token);

        // Once this is recieved, you know the call is now connected.
        if(eventResult.IsSuccess)
        {
            var addParticipantCompletedEvent = eventResult.SuccessResult;
            logger.LogInformation($"Call connected with Teams user with id: {addParticipantCompletedEvent.Participant}");

            //hangup the call as the aprticipant is already added. We wanted IVR to drop from the call.
            await callConnection.HangUpAsync(false);
        }
        else
        {
            logger.LogError($"Error adding participant: {eventResult.FailureResult.ResultInformation.Message}");
        }
        logger.LogInformation($"Participant added to call with connection id: {callConnectionId}");
    }
    catch (OperationCanceledException ex)
    {
        // Timeout exception happend!
        // Call likely was never answered.
    }
    catch (Exception ex)
    {
        logger.LogError($"Error adding participant: {ex.Message}");
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
        logger.LogInformation($"Event received: {JsonConvert.SerializeObject(@event, Formatting.Indented)}");

        if (cloudEvent.Type == "Microsoft.Communication.CallDisconnected")
        {
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
                var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                mediaService = new AcsMediaStreamingHandler(webSocket, builder.Configuration,client,callConnectionId);
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