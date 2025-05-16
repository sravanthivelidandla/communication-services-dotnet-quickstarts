using Azure.Communication.CallAutomation;
using CallAutomationOpenAI;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using OpenAI.RealtimeConversation;

namespace CallAutomation_AzOpenAI_Voice
{
    public class ToolHandler
    {
        private CallAutomationClient client;
        private string callConnectionId;
        private IConfiguration configuration;

        public ToolHandler(CallAutomationClient client, string callConnectionId, IConfiguration configuration)
#pragma warning restore OPENAI002 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
        {
            this.client = client;
            this.callConnectionId = callConnectionId;
            this.configuration = configuration;
           
           // this.//_logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ToolHandler>.Instance;
        }

        public async Task<string> HandleToolInvocation(string toolName, string parameters)
        {
            if (toolName == "validatePrescription")
            {
                Console.WriteLine($" <<< Validate tool invoked -- validating prescription!");
                var prescriptionDetails = JsonConvert.DeserializeObject<PrescriptionInput>(parameters);
                if (prescriptionDetails != null)
                {
                    // Call the API or execute the booking logic
                    var result = await ValidatePrescriptionDetails(
                        prescriptionDetails.prescriptionId,
                        prescriptionDetails.drugName,
                        prescriptionDetails.DOB);

                    Console.WriteLine($"Valid prescription details: {result}");
                    return result;
                }
                else
                {
                    Console.WriteLine("Invalid prescription parameters.");
                }
            }
            else if (toolName == "endConversation")
            {
                try
                {
                    Console.WriteLine($" <<< Validate tool invoked -- endConversation!");
                    return "Good bye!";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error adding participant: {ex.Message}");
                }
                return "";

            }
            else if (toolName == "speakToAgent")
            {
                Console.WriteLine($" <<< AddParticipantToolInvoked!");
                await AddParticipantAsync();

            }
            return "tool not invoked";
        }
        //public async Task<string> HandleToolInvocation(string toolName, string parameters)
        //{
        //    if (toolName == "validatePrescription")
        //    {
        //        Console.WriteLine($" <<< Validate tool invoked -- validating prescription!");
        //        var prescriptionDetails = JsonConvert.DeserializeObject<PrescriptionInput>(parameters);
        //        if (prescriptionDetails != null)
        //        {
        //            // Call the API or execute the booking logic
        //            var result = await ValidatePrescriptionDetails(
        //                prescriptionDetails.prescriptionId,
        //                prescriptionDetails.drugName,
        //                prescriptionDetails.DOB);

        //            Console.WriteLine($"Valid prescription details: {result}");
        //            return result;
        //        }
        //        else
        //        {
        //            Console.WriteLine("Invalid prescription parameters.");
        //        }
        //    }
        //    else if (toolName == "endConversation" || toolName == "speakToAgent")
        //    {
        //        try
        //        {
        //            if (toolName == "endConversation")
        //                Console.WriteLine($" <<< Tool invoked -- endConversation!");
        //            else
        //                Console.WriteLine($" <<< AddParticipantToolInvoked!");

        //            // Call analytics tools
        //            var callIntent = await HandleToolInvocation("getCallIntent", "{}");
        //            var callSummary = await HandleToolInvocation("summarizeCall", "{}");
        //            var sentiment = await HandleToolInvocation("analyzeSentiment", "{}");
        //            //_logger.LogInformation("CallIntent: {CallIntent}", callIntent);
        //            //_logger.LogInformation("CallSummary: {CallSummary}", callSummary);
        //            //_logger.LogInformation("Sentiment: {Sentiment}", sentiment);

        //            if (toolName == "speakToAgent")
        //            {
        //                await AddParticipantAsync();
        //            }
        //            if (toolName == "endConversation")
        //            {

        //            }
        //                return $"[Analytics] CallIntent: {callIntent}\nCallSummary: {callSummary}\nSentiment: {sentiment}";
        //        }
        //        catch (Exception ex)
        //        {
        //            Console.WriteLine($"Error in {toolName}: {ex.Message}");
        //        }
        //        return "";
        //    }
        //    //else if (toolName == "getCallIntent" || toolName == "summarizeCall" || toolName == "analyzeSentiment")
        //    //{
        //    //    var result =  await GetOpenAIToolResult(toolName, parameters);
        //    //    Console.WriteLine($" <<< Tool invoked -- {toolName} : {result}!");
        //    //    return result;
        //    //}
        //    return "tool not invoked";
        //}
        //private async Task<string> GetOpenAIToolResult(string toolName, string parameters)
        //{
        //    var session = m_aiSession;// Add a public getter for m_aiSession in AzureOpenAIService
        //                                               // Fix for OPENAI002: Suppress the diagnostic warning for 'OpenAI.RealtimeConversation.ConversationItem'
        //    #pragma warning disable OPENAI002

        //    // Fix for CS0117: Replace 'CreateFunctionCallInput' with 'CreateFunctionCall' as per the provided type signatures
        //    var item = ConversationItem.CreateFunctionCall(toolName, Guid.NewGuid().ToString(), parameters);
        //    await session.AddItemAsync(item);
        //    await session.StartResponseAsync();

        //    await foreach (ConversationUpdate update in session.ReceiveUpdatesAsync(CancellationToken.None))
        //    {
        //        if (update is ConversationItemStreamingFinishedUpdate finishedUpdate &&
        //            finishedUpdate.FunctionName == toolName)
        //        {
        //            return finishedUpdate.MessageContentParts?.FirstOrDefault()?.Text ?? "";
        //        }
        //    }
        //    return "";
        //}
        private async Task AddParticipantAsync()
        {
            try
            {
                var callConnection = client.GetCallConnection(callConnectionId);
                var callinviteToAdd = new CallInvite(new Azure.Communication.MicrosoftTeamsAppIdentifier("5d1d11ac-efac-408c-9a2b-3292993e89f1"));
                callinviteToAdd.CustomCallingContext.AddVoip("prescriptionId", "123456");

                AddParticipantResult addParticipantResult = await callConnection.AddParticipantAsync(new AddParticipantOptions(callinviteToAdd));
                
                // this will wait until CreateCall is completed or Timesout!
                AddParticipantEventResult eventResult = addParticipantResult.WaitForEventProcessor();

                // Once this is recieved, you know the call is now connected.
                if (eventResult.IsSuccess)
                {
                    var addParticipantCompletedEvent = eventResult.SuccessResult;
                    Console.WriteLine($"Call connected with Teams user with id: {addParticipantCompletedEvent.Participant}");
                    await callConnection.HangUpAsync(false);
                    //hangup the call as the aprticipant is already added. We wanted IVR to drop from the call.

                }
                else
                {
                    Console.WriteLine($"Error adding participant: {eventResult.FailureResult.ResultInformation.Message}");
                }
                Console.WriteLine($"Participant added to call with connection id: {callConnectionId}");
            }
            catch (OperationCanceledException ex)
            {
                // Timeout exception happend!
                // Call likely was never answered.
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding participant: {ex.Message}");
            }
        }

        private async Task<string> ValidatePrescriptionDetails(string prescriptionId, string drugName, string dateofBirth)
        {
            string storageConnectionString = this.configuration.GetValue<string>("AzureStorageAccountEndpoint") ?? throw new ArgumentNullException("AzureStorageAccountEndpoint");
            string tableName = "PrescriptionManager";

            if (!DateOnly.TryParse(dateofBirth, out _))
            {
                return "Invalid Date of Birth. Please provide a valid Date of Birth.";
            }

            var tableStorageService = new TableStorageService(storageConnectionString, tableName);
            var retrievedPrescription = await tableStorageService.GetPrescriptionAsync(prescriptionId);
            Console.WriteLine($"Retrieved Prescription: {retrievedPrescription?.DrugName}");

            PrescriptionDetails prescriptionDetails = new PrescriptionDetails();
            if (retrievedPrescription != null)
            {
                prescriptionDetails.DOB = retrievedPrescription.DOB;
                prescriptionDetails.RefillsPending = retrievedPrescription.RefillsPending;
                prescriptionDetails.PhoneNumber = retrievedPrescription.PhoneNumber;
                prescriptionDetails.DrugName = retrievedPrescription.DrugName;
                prescriptionDetails.PrescriptionId = retrievedPrescription.PrescriptionId;
            };

            if (prescriptionDetails?.PrescriptionId == prescriptionId)
            {
                string pickupDate = GetPickupDate();
                return string.Format("Prescription Validation Successful. Renewal for drug {3} is placed and you have {0} refills for renewal. Your Prescription will be available by {1} at {2}.", prescriptionDetails?.RefillsPending, GetPickupDate(), GetPickupTime(), drugName);
            }
            else
            {
                return "Prescription Id does not match. Could you please provide a valid prescriptionId";
            }
        }

        private string GetPickupDate()
        {
            DateTime futureDateTime = DateTime.Now.AddDays(1).AddHours(3);
            return futureDateTime.ToString("dd-MMM HH");
        }

        private string GetPickupTime()
        {
            DateTime futureDateTime = DateTime.Now.AddDays(1).AddHours(3);
            return futureDateTime.ToString("htt").Replace("AM", "AM").Replace("PM", "PM");
        }
    }
}

public class PrescriptionInput
{
    public string prescriptionId { get; set; }
    public string drugName { get; set; }
    public string DOB { get; set; }
}

