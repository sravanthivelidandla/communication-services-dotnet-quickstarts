using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Newtonsoft.Json.Linq;

public static class Helper
{
    public static JsonObject GetJsonObject(BinaryData data)
    {
        return JsonNode.Parse(data).AsObject();
    }
    public static string GetCallerId(JsonObject jsonObject)
    {
        return (string)(jsonObject["from"]["rawId"]);
    }

    public static string GetIncomingCallContext(JsonObject jsonObject)
    {
        return (string)jsonObject["incomingCallContext"];
    }

    public static string DecompressGzipString(string text)
    {
        if (string.IsNullOrEmpty(text) || !IsBase64String(text))
        {
            throw new FormatException("The input is not a valid Base-64 string.");
        }

        var text1 = Convert.FromBase64String(text);
        using (var inputStream = new MemoryStream(text1))
        using (var outputStream = new MemoryStream())
        {
            using (var gzipStream = new GZipStream(inputStream, CompressionMode.Decompress))
            {
                gzipStream.CopyTo(outputStream);
            }

            return Encoding.UTF8.GetString(outputStream.ToArray());
        }
    }

    private static bool IsBase64String(string base64)
    {
        Span<byte> buffer = new Span<byte>(new byte[base64.Length]);
        return Convert.TryFromBase64String(base64, buffer, out _);
    }

    //private void GetCallNotificationProperties(string calleeMri, string incomingCallContext)
    //{
    //    var callNotificationString = DecompressGzipString(incomingCallContext);
    //    bool isPstnCall = false;
    //    if (!string.IsNullOrWhiteSpace(callNotificationString))
    //    {
    //        JObject callNotificationObject = JObject.Parse(callNotificationString);
    //        if (callNotificationObject != null)
    //        {
    //            //if (!string.IsNullOrWhiteSpace(calleeMri) && calleeMri.EndsWith(EmptyGuidString))
    //            //{
    //            //    var targetMri = (string)callNotificationObject.SelectToken(OriginalTargetToken);
    //            //    isPstnCall = true;
    //            //}

    //            var callId = (string)callNotificationObject.SelectToken(ConversationControllerToken);
    //            var serverCallId = callId != null ? EncodeStringToBase64(callId) : callId;
    //            var callConnectionId = (string)callNotificationObject.SelectToken(CallConnectionIdToken);
    //            var customContext = GetCustomContextHeaders(callNotificationObject);
    //        }
    //    }
    //}
}