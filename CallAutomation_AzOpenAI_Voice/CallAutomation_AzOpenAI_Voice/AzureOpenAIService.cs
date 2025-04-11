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
using Microsoft.Extensions.Logging;
using CallAutomation_AzOpenAI_Voice;

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
        private CallAutomationClient client;
        private string callConnectionId;
        private ToolHandler toolHandler;

        public AzureOpenAIService(AcsMediaStreamingHandler mediaStreaming, IConfiguration configuration, CallAutomationClient client, string callConnectionId)
        {            
            m_mediaStreaming = mediaStreaming;
            m_cts = new CancellationTokenSource();
            m_aiSession =  CreateAISessionAsync(configuration).GetAwaiter().GetResult();
            m_memoryStream = new MemoryStream();
            this.configuration = configuration;
            this.client = client;
            this.callConnectionId = callConnectionId;
            this.toolHandler = new ToolHandler(client, callConnectionId, configuration);
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
                Tools = { validatePrescriptionTool, SpeakToAgent, endConversation },
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
                    }

                    // Audio transcript  updates contain the incremental text matching the generated
                    // output audio.
                    if (update is ConversationItemStreamingAudioTranscriptionFinishedUpdate outputTranscriptDeltaUpdate)
                    {
                        Console.Write(outputTranscriptDeltaUpdate.Transcript);
                    }

                    if(update is ConversationItemStreamingAudioFinishedUpdate test)
                    {
                        Console.WriteLine("AI has finished responding and waiting for user input");
                    }
                    

                    if (update is ConversationItemStreamingFinishedUpdate itemStreamingFinishedUpdate)
                    {
                        if(!string.IsNullOrEmpty(itemStreamingFinishedUpdate.FunctionName))
                        {
                            var result = await toolHandler.HandleToolInvocation(itemStreamingFinishedUpdate.FunctionName, itemStreamingFinishedUpdate.FunctionCallArguments);

                            ConversationItem functionOutputItem = ConversationItem.CreateFunctionCallOutput(
                              callId: itemStreamingFinishedUpdate.FunctionCallId,
                              output: result);
                            await m_aiSession.AddItemAsync(functionOutputItem);
                            await m_aiSession.StartResponseAsync();
                            await m_mediaStreaming.SendMessageAsync(result);

                            if (itemStreamingFinishedUpdate.FunctionName == "endConversation")
                            {
                                Thread.Sleep(3000);
                                await hangUp();
                            }
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

        private async Task hangUp()
        {
            var callConnection = client.GetCallConnection(callConnectionId);
            if (callConnection != null)
            {
                _ = await callConnection.HangUpAsync(true);
            }
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

        ConversationFunctionTool validatePrescriptionTool = new()
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
                            "description": "The date of birth. "
                        }
                    },
                    "required": ["prescriptionId", "drugName", "DOB"]
                }
                """)
        };

        ConversationFunctionTool SpeakToAgent = new()
        {
            Name = "speakToAgent",
            Description = "Invoked when the user wants to talk or reach out or speak to a pharmacist or doctor.",
            Parameters = BinaryData.FromString("{}")
        };


        ConversationFunctionTool endConversation = new()
        {
            Name = "endConversation",
            Description = " Leave the call when you say goodbye or caller says goodbye or Thank you or The user has nothing for you to act upon",
            Parameters = BinaryData.FromString("{}")
        };
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

    public class PrescriptionInput
    {
        public string prescriptionId { get; set; }
        public string drugName { get; set; }
        public string DOB { get; set; }
    }
}