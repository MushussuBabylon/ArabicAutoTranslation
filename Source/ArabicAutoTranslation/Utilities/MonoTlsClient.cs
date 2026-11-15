using System;
using System.IO;
using System.Net;
using System.Text;
#if !RW17 && !AT_NO_MONO_SECURITY
using Mono.Security.Interface;
#endif

namespace ArabicAutoTranslation.Utilities
{
#if RW17 || AT_NO_MONO_SECURITY
    // MonoTLS Client is not needed on 1.7 - native TLS will probably be used there, and that should not be a problem
    public static class MonoTlsClient
    {
        public static bool IsSupported => false;

        public static void Disable() { }

        public static string Get(string url, string userAgent, int timeoutMs, out int statusCode)
        {
            throw new NotSupportedException("Mono TLS is not available on RimWorld 1.7");
        }
    }
#else

    public static class MonoTlsClient
    {
        private static readonly CipherSuiteCode[] BrowserLikeCiphers =
        {
            CipherSuiteCode.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
            CipherSuiteCode.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
            CipherSuiteCode.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
            CipherSuiteCode.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
            CipherSuiteCode.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
            CipherSuiteCode.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
        };

        private const string ConnectionGroup = "ArabicAutoTranslation.BrowserLikeTls";

        private static volatile bool _disabled;
        private static MonoTlsSettings _settings;

        public static bool IsSupported => !_disabled;

        public static void Disable() => _disabled = true;

        public static string Get(string url, string userAgent, int timeoutMs, out int statusCode)
        {
            if (_settings == null)
            {
                var settings = MonoTlsSettings.CopyDefaultSettings();
                settings.EnabledCiphers = BrowserLikeCiphers;
                _settings = settings;
            }

            var provider = MonoTlsProviderFactory.GetProvider();
            var request = MonoTlsProviderFactory.CreateHttpsRequest(new Uri(url), provider, _settings);
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.UserAgent = userAgent;
            request.ConnectionGroupName = ConnectionGroup;

            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)request.GetResponse();
            }
            catch (WebException e) when (e.Response is HttpWebResponse errorResponse)
            {
                response = errorResponse;
            }

            using (response)
            using (var stream = response.GetResponseStream())
            {
                statusCode = (int)response.StatusCode;
                if (stream == null) return string.Empty;
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }
    }
#endif
}
