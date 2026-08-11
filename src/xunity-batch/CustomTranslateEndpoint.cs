using System;
using System.Net;
using System.Text;
using XUnity.AutoTranslator.Plugin.Core.Configuration;
using XUnity.AutoTranslator.Plugin.Core.Constants;
using XUnity.AutoTranslator.Plugin.Core.Endpoints;
using XUnity.AutoTranslator.Plugin.Core.Endpoints.Http;
using XUnity.AutoTranslator.Plugin.Core.Web;

namespace CustomTranslate
{
    internal sealed class CustomTranslateEndpoint : HttpEndpoint
    {
        private string endpoint;

        public override string Id { get { return "CustomTranslate"; } }
        public override string FriendlyName { get { return "GameTranslator Batch"; } }
        public override int MaxTranslationsPerRequest { get { return 100; } }

        public override void Initialize(IInitializationContext context)
        {
            endpoint = context.GetOrCreateSetting("Custom", "Url", "");
            if (string.IsNullOrEmpty(endpoint))
                throw new EndpointInitializationException("Custom.Url is required.");
        }

        public override void OnCreateRequest(IHttpRequestCreationContext context)
        {
            var encoded = Array.ConvertAll(context.UntranslatedTexts,
                value => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
            var url = endpoint + "?from=" + Uri.EscapeDataString(context.SourceLanguage)
                + "&to=" + Uri.EscapeDataString(context.DestinationLanguage);
            var request = new XUnityWebRequest("POST", url, string.Join("\n", encoded));
            request.Headers[HttpRequestHeader.ContentType] = "text/plain; charset=utf-8";
            context.Complete(request);
        }

        public override void OnExtractTranslation(IHttpTranslationExtractionContext context)
        {
            var translations = Array.ConvertAll(context.Response.Data.Split('\n'),
                value => Encoding.UTF8.GetString(Convert.FromBase64String(value.TrimEnd('\r'))));
            if (translations.Length != context.UntranslatedTexts.Length)
                context.Fail("GameTranslator returned a different number of translations.");
            context.Complete(translations);
        }

    }
}
