using System.Net.WebSockets;
using System.Threading.Channels;
using OpenAI.RealtimeConversation;
using Azure.AI.OpenAI;
using System.ClientModel;
using Azure.Communication.CallAutomation;
using Newtonsoft.Json;
using System.Text;
using Microsoft.CognitiveServices.Speech;
using OpenAI;
using Microsoft.DevTunnels.Ssh.Algorithms;
using System.Dynamic;

#pragma warning disable OPENAI002
namespace CallAutomationOpenAI
{
    public class AzureOpenAIService
    {
        private WebSocket m_webSocket;
        private CancellationTokenSource m_cts;
        private RealtimeConversationSession m_aiSession;
        private AcsMediaStreamingHandler m_mediaStreaming;
        private MemoryStream m_memoryStream;
        private string m_answerPromptSystemTemplate = "You are an AI assistant that helps people for renewing prescriptions.";
        private IConfiguration configuration;
        private string finishToolName = "user_wants_to_finish_conversation";
        private string addParticipantTool = "addParticipant";

        public AzureOpenAIService(AcsMediaStreamingHandler mediaStreaming, IConfiguration configuration)
        {            
            m_mediaStreaming = mediaStreaming;
            m_cts = new CancellationTokenSource();
            m_aiSession =  CreateAISessionAsync(configuration).GetAwaiter().GetResult();
            m_memoryStream = new MemoryStream();
            this.configuration = configuration;
        }

        private async Task<RealtimeConversationSession> CreateAISessionAsync(IConfiguration configuration)
        {
            var openAiKey = configuration.GetValue<string>("AzureOpenAIServiceKey");
            ArgumentNullException.ThrowIfNullOrEmpty(openAiKey);

            var openAiUri = configuration.GetValue<string>("AzureOpenAIServiceEndpoint");
            ArgumentNullException.ThrowIfNullOrEmpty(openAiUri);

            var openAiModelName = configuration.GetValue<string>("AzureOpenAIDeploymentModelName");
            ArgumentNullException.ThrowIfNullOrEmpty(openAiModelName);
            var systemPrompt = configuration.GetValue<string>("SystemPrompt") ?? m_answerPromptSystemTemplate;
            ArgumentNullException.ThrowIfNullOrEmpty(openAiUri);

            var aiClient = new AzureOpenAIClient(new Uri(openAiUri), new ApiKeyCredential(openAiKey));
            var RealtimeCovnClient = aiClient.GetRealtimeConversationClient(openAiModelName);
            var session =  await RealtimeCovnClient.StartConversationSessionAsync();

            // We'll add a simple function tool that enables the model to interpret user input to figure out when it
            // might be a good time to stop the interaction.
            ConversationFunctionTool finishConversationTool = new()
            {
                Name = finishToolName,
                Description = "Invoked when the user says goodbye, expresses being finished, or otherwise seems to want to stop the interaction.",
                Parameters =  BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "customer_name": {
                            "type": "string",
                            "description": "The customer's name (optional)."
                        },
                        "time_of_day": {
                            "type": "string",
                            "enum": ["morning", "afternoon", "evening"],
                            "description": "The time of day to personalize the greeting."
                        }
                    },
                    "required": [""]
                }
                """)
              };
       

            ConversationFunctionTool startConversationTool = new()
            {
                Name = "get_greeting_message",
                Description = "Generates a greeting message for the user.",
                Parameters = BinaryData.FromString("{}")
            };

            ConversationFunctionTool SpeakToAgent = new()
            {
                Name = "speakToAgent",
                Description = "Invoked when the user wants to talk or reach out or speak to a pharmacist or doctor.",
                Parameters = BinaryData.FromString("{}")
            };

            // Session options control connection-wide behavior shared across all conversations,
            // including audio input format and voice activity detection settings.
            ConversationSessionOptions sessionOptions = new()
            {
                Instructions = systemPrompt,
                Voice = ConversationVoice.Alloy,
                InputAudioFormat = ConversationAudioFormat.Pcm16,
                OutputAudioFormat = ConversationAudioFormat.Pcm16,
                InputTranscriptionOptions = new()
                {
                    Model = "whisper-1",
                },
                TurnDetectionOptions = ConversationTurnDetectionOptions.CreateServerVoiceActivityTurnDetectionOptions(0.5f, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500)),
                Tools = { finishConversationTool , validatePrescriptionTool(), SpeakToAgent },
            };

            await session.ConfigureSessionAsync(sessionOptions);
            return session;
        }

        // Loop and wait for the AI response
        private async Task GetOpenAiStreamResponseAsync()
        {
            try
            {
                await m_aiSession.StartResponseAsync();
                await foreach (ConversationUpdate update in m_aiSession.ReceiveUpdatesAsync(m_cts.Token))
                {
                   
                    if (update is ConversationSessionStartedUpdate sessionStartedUpdate)
                    {
                        Console.WriteLine($"<<< Session started. ID: {sessionStartedUpdate.SessionId}");
                        await m_mediaStreaming.SendMessageAsync("Hello!! This is a test mesage");
                        Console.WriteLine();
                    }
                   

                    if (update is ConversationInputSpeechStartedUpdate speechStartedUpdate)
                    {
                        Console.WriteLine(
                            $"  -- Voice activity detection started at {speechStartedUpdate.AudioStartTime} ms");
                        // Barge-in, send stop audio
                        var jsonString = OutStreamingData.GetStopAudioForOutbound();
                        await m_mediaStreaming.SendMessageAsync(jsonString);
                    }

                    if (update is ConversationInputSpeechFinishedUpdate speechFinishedUpdate)
                    {
                        Console.WriteLine(
                            $"  -- Voice activity detection ended at {speechFinishedUpdate.AudioEndTime} ms");
                    }

                    if (update is ConversationItemStreamingStartedUpdate itemStartedUpdate)
                    {
                        Console.WriteLine($"  -- Begin streaming of new item");
                    }

                    // conversation.item.input_audio_transcription.completed will only arrive if input transcription was
                    // configured for the session. It provides a written representation of what the user said, which can
                    // provide good feedback about what the model will use to respond.
                    if (update is ConversationInputTranscriptionFinishedUpdate transcriptionFinishedUpdate)
                    {
                        Console.WriteLine($" >>> USER: {transcriptionFinishedUpdate.Transcript}");
                        string transcription = transcriptionFinishedUpdate.Transcript;
                        
                        processTranscription(transcription);

                        
                    }

                    // Audio transcript  updates contain the incremental text matching the generated
                    // output audio.
                    if (update is ConversationItemStreamingAudioTranscriptionFinishedUpdate outputTranscriptDeltaUpdate)
                    {
                        Console.Write(outputTranscriptDeltaUpdate.Transcript);
                       
                        //if (outputTranscriptDeltaUpdate.Transcript.Contains("transfer", StringComparison.OrdinalIgnoreCase))
                        //{
                        //    //await m_aiSession.CancelResponseAsync();
                        //    //await m_aiSession.ClearInputAudioAsync();
                        //    await TransferToAgentAsync(Guid.NewGuid().ToString());
                        //}
                    }

                    

                    if (update is ConversationItemStreamingFinishedUpdate itemStreamingFinishedUpdate)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"  -- Item streaming finished, item_id={itemStreamingFinishedUpdate.ItemId}");

                        if (itemStreamingFinishedUpdate.FunctionName == finishToolName)
                        {
                            Console.WriteLine($" <<< Finish tool invoked -- ending conversation!");
                            break;
                        }

                        if(itemStreamingFinishedUpdate.FunctionName == "validatePrescription")
                        {
                            Console.WriteLine($" <<< Validate tool invoked -- validating prescription!");
                            var result = await HandleToolInvocation(itemStreamingFinishedUpdate.FunctionName, itemStreamingFinishedUpdate.FunctionCallArguments);

                            ConversationItem functionOutputItem = ConversationItem.CreateFunctionCallOutput(
                              callId: itemStreamingFinishedUpdate.FunctionCallId,
                              output: result);
                            await m_aiSession.AddItemAsync(functionOutputItem);
                            //await m_aiSession.ClearInputAudioAsync();
                            //await m_aiSession.CancelResponseAsync();
                            await m_aiSession.StartResponseAsync();
                            await m_mediaStreaming.SendMessageAsync(result);

                        }

                        //if (itemStreamingFinishedUpdate.FunctionName == "get_greeting_message")
                        //{
                        //    Console.WriteLine($" <<< Validate tool invoked -- get_greeting_message!");
                        //    var result = "Welcome To Prescription Renewal service.You are calling from 1234567. If this is the phone number associated with your prescription please say “Yes” otherwise please say or enter the phone number associated with your prescription ";

                        //    ConversationItem functionOutputItem = ConversationItem.CreateFunctionCallOutput(
                        //      callId: itemStreamingFinishedUpdate.FunctionCallId,
                        //      output: result);
                        //    await m_aiSession.AddItemAsync(functionOutputItem);
                        //    await m_aiSession.StartResponseAsync();
                        //    await m_mediaStreaming.SendMessageAsync(result);

                        //}

                        if (itemStreamingFinishedUpdate.FunctionName == "speakToAgent")
                        {
                            Console.WriteLine($" <<< TransferToAgentToolInvoked!");
                            await TransferToAgentAsync(Guid.NewGuid().ToString());
                        }

                        else if (itemStreamingFinishedUpdate.MessageContentParts?.Count > 0)
                        {
                            Console.Write($"    + [{itemStreamingFinishedUpdate.MessageRole}]: ");
                            foreach (ConversationContentPart contentPart in itemStreamingFinishedUpdate.MessageContentParts)
                            {
                                Console.Write(contentPart.AudioTranscript);
                            }
                            Console.WriteLine();
                        }
                       
                    }

                    // Audio delta updates contain the incremental binary audio data of the generated output
                    // audio, matching the output audio format configured for the session.
                    if (update is ConversationItemStreamingPartDeltaUpdate deltaUpdate)
                    {
                        if( deltaUpdate.AudioBytes != null)
                        {
                            var jsonString = OutStreamingData.GetAudioDataForOutbound(deltaUpdate.AudioBytes.ToArray());
                            await m_mediaStreaming.SendMessageAsync(jsonString);
                        }
                    }

                    if (update is ConversationItemStreamingTextFinishedUpdate itemFinishedUpdate)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"  -- Item streaming finished, response_id={itemFinishedUpdate.ResponseId}");
                    }

                    if (update is ConversationInputTranscriptionFinishedUpdate transcriptionCompletedUpdate)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"  -- User audio transcript: {transcriptionCompletedUpdate.Transcript}");
                        Console.WriteLine();
                    }

                    if (update is ConversationResponseFinishedUpdate turnFinishedUpdate)
                    {
                        Console.WriteLine($"  -- Model turn generation finished. Status: {turnFinishedUpdate.Status}");
                        if (turnFinishedUpdate.CreatedItems.Any(item => item.FunctionName?.Length > 0))
                        {
                            Console.WriteLine($"  -- Ending client turn for pending tool responses");
                            
                        }
                      
                    }

                    if (update is ConversationErrorUpdate errorUpdate)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"ERROR: {errorUpdate.Message}");
                        break;
                    }
                }
            }
            catch (OperationCanceledException e)
            {
                Console.WriteLine($"{nameof(OperationCanceledException)} thrown with message: {e.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception during ai streaming -> {ex}");
            }
        }

        private void processTranscription(string transcription)
        {
            if (transcription.Contains("transfer", StringComparison.OrdinalIgnoreCase))
            {
                //await m_aiSession.CancelResponseAsync();
                //await m_aiSession.ClearInputAudioAsync();
                //await TransferToAgentAsync(Guid.NewGuid().ToString());
            }
            else if (transcription.Contains("validate", StringComparison.OrdinalIgnoreCase))
            {
                
            }
        }


        private async Task TransferToAgentAsync(string callConnectionId)
        {
            var transferRequest = new TransferRequest
            {
                CallConnectionId = callConnectionId,
                TargetPhoneNumber = "target-agent-phone-number" // Replace with the actual target phone number
            };

            var jsonString = JsonConvert.SerializeObject(transferRequest);
            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            using var httpClient = new HttpClient();
            var appBaseUrl = this.configuration.GetValue<string>("AppServiceUri")?.TrimEnd('/');
            var response = await httpClient.PostAsync(appBaseUrl + "/api/addParticipant", content);
            //var response = await httpClient.PostAsync(appBaseUrl + "/api/transferCall", content);

            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("Call transferred successfully.");
            }
            else
            {
                Console.WriteLine("Failed to transfer call.");
            }

        }

        private async Task<string> HandleToolInvocation(string toolName, string parameters)
        {
            if (toolName == "validatePrescription")
            {
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
            else
            {
                Console.WriteLine($"Unknown tool: {toolName}");
            }
            return "tool not invoked";
        }


        private async Task<string> ValidatePrescriptionDetails(string prescriptionId, string drugName, string dateofBirth)
        {
            var prescriptionRequest = new ValidatePrescriptionRequest
            {
                prescriptionId = prescriptionId
            };

            var jsonString = JsonConvert.SerializeObject(prescriptionRequest);
            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");

            using var httpClient = new HttpClient();
            var appBaseUrl = this.configuration.GetValue<string>("AppServiceUri")?.TrimEnd('/');
            var response = await httpClient.PostAsync(appBaseUrl + "/api/validatePrescription", content);

            if (response.IsSuccessStatusCode)
            {
                var responseData = await response.Content.ReadAsStringAsync();
                var parsedResponse = JsonConvert.DeserializeObject<ValidatePrescriptionResponse>(responseData);

                if (parsedResponse?.PrescriptionEntity.PrescriptionId == prescriptionId)
                {
                    
                    return "Prescription Validation Successful. Renewal for prescription is placed and will be available by 1AM.";
                    //if (parsedResponse?.DrugName == drugName)
                    //{
                    //    Console.WriteLine("valid details provided");
                    //    return "Prescription Validation Successful. Renewal for prescription is placed and will be available by 1AM.";
                    //}
                    //else
                    //{
                    //    return "Drug name does not match.";
                    //}
                }
                else
                {
                    return "Prescription Id does not match.";
                }
            }
            else
            {
                Console.WriteLine("Failed to validate prescription.");
                return "Validation Failed";
            }

            return " Nothing happened";
        }

        public void StartConversation()
        {
            _ = Task.Run(async () => await GetOpenAiStreamResponseAsync());
        }

        public async Task SendAudioToExternalAI(MemoryStream memoryStream)
        {
            await m_aiSession.SendInputAudioAsync(memoryStream);
        }

        public void Close()
        {
            m_cts.Cancel();
            m_cts.Dispose();
            m_aiSession.Dispose();
        }
        public async Task RemoveBotFromCallAsync()
        {
            try
            {
                // Stop the AI session response
                await m_aiSession.CancelResponseAsync();

                // Clear any input audio
                await m_aiSession.ClearInputAudioAsync();

                // Send a message to stop audio streaming
                var jsonString = OutStreamingData.GetStopAudioForOutbound();
                await m_mediaStreaming.SendMessageAsync(jsonString);

                Console.WriteLine("Bot has been removed from the call and audio streaming has been stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception while removing bot from call -> {ex}");
            }
        }

        private static ConversationFunctionTool CreateSampleWeatherTool()
        {
            return new ConversationFunctionTool()
            {
                Name = "get_weather_for_location",
                Description = "gets the weather for a location",
                Parameters = BinaryData.FromString("""
            {
              "type": "object",
              "properties": {
                "location": {
                  "type": "string",
                  "description": "The city and state, e.g. San Francisco, CA"
                },
                "unit": {
                  "type": "string",
                  "enum": ["c","f"]
                }
              },
              "required": ["location","unit"]
            }
            """)
            };
        }

        private static ConversationFunctionTool validatePrescriptionTool()
        {
            return new ConversationFunctionTool()
            {
                Name = "validatePrescription",
                Description = "Once the user provides all the inputs like PrescriptionId, DrugName and Date of Birth, Validates the prescription based on the PrescriptionId, DrugName and DateOfBirth",
                Parameters = BinaryData.FromString("""
                {
                    "type": "object",
                    "properties": {
                        "prescriptionId": {
                            "type": "string",
                            "description": "PrescriptionId of the user"
                        },
                        "drugName": {
                            "type": "string",
                            "description": "Name of the drug. Ex : Advil"
                        },
                        "DOB": {
                            "type": "string",
                            "description": "The date of birth in YYYY-MM-DD format"
                        }
                    },
                    "required": ["prescriptionId", "drugName", "DOB"]
                }
                """)
            };

        }

        


    }

    public class PrescriptionInput
    {
        public string prescriptionId { get; set; }
        public string drugName { get; set; }
        public string DOB { get; set; }
    }
}