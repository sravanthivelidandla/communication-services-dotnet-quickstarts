using System.Text.Json.Serialization;
using Azure.Communication;
using Azure.Communication.CallAutomation;
using CallAutomationOpenAI;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using OpenAI.RealtimeConversation;

namespace CallAutomation_AzOpenAI_Voice
{
    public class ToolHandler
    {
        private CallAutomationClient client;
        private string callConnectionId;
        private IConfiguration configuration;
        private CustomCallingContext customContext;
        private PrescriptionDetails prescriptionDetails;

        public ToolHandler(CallAutomationClient client, string callConnectionId, IConfiguration configuration,CustomCallingContext customContext)
        {
            this.client = client;
            this.callConnectionId = callConnectionId;
            this.configuration = configuration;
            this.customContext = customContext;
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
                    //var analytics = await ProcessCallAnalytics();
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
                var callAnalytics = JsonConvert.DeserializeObject<CallAnalytics>(parameters);
                await AddParticipantAsync(callAnalytics);

            }
            return "tool not invoked";
        }       
       
        private async Task AddParticipantAsync(CallAnalytics callAnalytics)
        {
            try
            {
                var callConnection = client.GetCallConnection(callConnectionId);
                var callinviteToAdd = new CallInvite(new Azure.Communication.MicrosoftTeamsAppIdentifier("5d1d11ac-efac-408c-9a2b-3292993e89f1"));

                TeamsPhoneCallDetails teamsPhoneCallDetails = customContext.TeamsPhoneCallDetails;

                if (prescriptionDetails != null)
                {

                    if (teamsPhoneCallDetails == null)
                    {
                        teamsPhoneCallDetails = new TeamsPhoneCallDetails();
                    }

                    //just to test if we are able to see whether the agent is getting ring.
//                    teamsPhoneCallDetails.TeamsPhoneSourceDetails = null;
                    //todo : fix this in PMA :
                    if (teamsPhoneCallDetails.TeamsPhoneSourceDetails != null)
                        teamsPhoneCallDetails.TeamsPhoneSourceDetails.Language = "en-Us";
                    ////Add the source details as a placeholder
                    //teamsPhoneCallDetails.TeamsPhoneSourceDetails = new TeamsPhoneSourceDetails(
                    //    new MicrosoftTeamsAppIdentifier("ee9b35e5-f4a1-458d-8c19-aa4a004ecf2f"), "en-us", "open");

                    //pass the prescription details here to read all the values from the prescription entity
                    teamsPhoneCallDetails.TeamsPhoneCallerDetails = new TeamsPhoneCallerDetails(
                        new PhoneNumberIdentifier(prescriptionDetails?.PhoneNumber), prescriptionDetails?.Name, prescriptionDetails?.PhoneNumber)
                    {
                        IsAuthenticated = true,
                        ScreenPopUrl = prescriptionDetails?.ScreenPopUpUrl,
                        RecordId = prescriptionDetails?.RecordId
                    };

                    //teamsPhoneCallDetails.TeamsPhoneCallerDetails.AddAdditionalCallerInformation("PrescriptionId", prescriptionDetails?.PrescriptionId);
                    //teamsPhoneCallDetails.TeamsPhoneCallerDetails.AddAdditionalCallerInformation("DrugName", prescriptionDetails?.DrugName);
                    //teamsPhoneCallDetails.TeamsPhoneCallerDetails.AddAdditionalCallerInformation("DOB", prescriptionDetails?.DOB);
                    //teamsPhoneCallDetails.TeamsPhoneCallerDetails.AddAdditionalCallerInformation("RefillsPending", prescriptionDetails?.RefillsPending);
                    //teamsPhoneCallDetails.TeamsPhoneCallerDetails.AddAdditionalCallerInformation("Address", "14004 Abc, BC");

                    teamsPhoneCallDetails.CallSentiment = callAnalytics.callSentiment;
                    teamsPhoneCallDetails.CallTopic = callAnalytics.callIntent;
                    teamsPhoneCallDetails.CallContext = callAnalytics.callSummary;
                    teamsPhoneCallDetails.SuggestedActions = callAnalytics.suggestedActions;

                    callinviteToAdd.CustomCallingContext?.SetTeamsPhoneCallDetails(teamsPhoneCallDetails);
                }

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

            prescriptionDetails = new PrescriptionDetails();
            if (retrievedPrescription != null)
            {
                prescriptionDetails.DOB = retrievedPrescription.DOB;
                prescriptionDetails.RefillsPending = retrievedPrescription.RefillsPending;
                prescriptionDetails.PhoneNumber = retrievedPrescription.PhoneNumber;
                prescriptionDetails.DrugName = retrievedPrescription.DrugName;
                prescriptionDetails.PrescriptionId = retrievedPrescription.PrescriptionId;
                prescriptionDetails.ScreenPopUpUrl = retrievedPrescription.ScreenPopUpUrl;
                prescriptionDetails.RecordId = retrievedPrescription.RecordId;
                prescriptionDetails.Name = retrievedPrescription.Name;
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

public class CallAnalytics
{
    [JsonPropertyName("intent")]
    public string callIntent { get; set; }
        
    [JsonPropertyName("summary")]
    public string callSummary { get; set; }
   
    [JsonPropertyName("sentiment")]
    public string callSentiment { get; set; }

    [JsonPropertyName("suggestedActions")]  
    public string suggestedActions { get; set; }
}

