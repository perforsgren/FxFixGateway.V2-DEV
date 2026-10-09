using System.Net;
using System.Text;
using System.Xml.Linq;

namespace FxFixGateway.Infrastructure.PostMarker
{
    /// <summary>
    /// Minimal SOAP 1.1 client for PostMarker's web service (PostMarker.asmx, document/literal,
    /// namespace http://tempuri.org/). Replaces Tullett's SDK (PostMarker.dll), which needs .NET
    /// Framework (System.Web.Services, AppDomains) and cannot run on .NET 8. Only the calls the
    /// gateway uses are implemented; requests match what the SDK sends.
    /// </summary>
    public sealed class PostMarkerSoapClient : IDisposable
    {
        private static readonly XNamespace SoapNs = "http://schemas.xmlsoap.org/soap/envelope/";
        private static readonly XNamespace XsiNs = "http://www.w3.org/2001/XMLSchema-instance";
        private static readonly XNamespace Tns = "http://tempuri.org/";

        // What the SDK's Accept(sequenceNumber, notes) sends as approvingUADID.
        private const int NoApprovingUser = -1;

        private readonly HttpClient _http;
        private readonly Uri _serviceUri;

        public PostMarkerSoapClient(PostMarkerSettings settings)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(settings.ProxyAddress))
            {
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(settings.ProxyAddress) { UseDefaultCredentials = true };
            }

            // GetNext holds the call open for up to its timeoutSeconds (20 s) while waiting for deals.
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
            _serviceUri = new Uri(settings.ServiceUrl);
        }

        /// <summary>Logs in and returns the session ID used by every other call.</summary>
        public async Task<string> ConnectAsync(string userName, string password, int clientSystemId, bool testFlag, CancellationToken ct)
        {
            var response = await CallAsync("Connect", ct,
                new XElement(Tns + "userName", userName),
                new XElement(Tns + "password", password),
                new XElement(Tns + "clientSystemID", clientSystemId),
                new XElement(Tns + "testFlag", testFlag)).ConfigureAwait(false);

            var sessionId = (string?)response.Element(Tns + "ConnectResult");
            if (string.IsNullOrWhiteSpace(sessionId))
                throw new PostMarkerException("Connect returned no session ID.");

            return sessionId;
        }

        /// <summary>Waits up to <paramref name="timeoutSeconds"/> for new payloads and returns them (often none).</summary>
        public async Task<IReadOnlyList<PostMarkerPayload>> GetNextAsync(string sessionId, int timeoutSeconds, CancellationToken ct)
        {
            var response = await CallAsync("GetNext", ct,
                new XElement(Tns + "sessionID", sessionId),
                new XElement(Tns + "timeoutSeconds", timeoutSeconds)).ConfigureAwait(false);

            var result = response.Element(Tns + "GetNextResult");
            if (result == null)
                return Array.Empty<PostMarkerPayload>();

            return result.Elements(Tns + "PMXMLPayload")
                .Where(e => (string?)e.Attribute(XsiNs + "nil") != "true")
                .Select(e => new PostMarkerPayload(
                    PayloadId: (int?)e.Element(Tns + "PayloadID") ?? 0,
                    SequenceNumber: (int?)e.Element(Tns + "SequenceNo") ?? 0,
                    Status: (string?)e.Element(Tns + "Status") ?? string.Empty,
                    StatusVersion: (int?)e.Attribute("StatusVersion") ?? 0,
                    Xml: (string?)e.Element(Tns + "XML") ?? string.Empty))
                .ToList();
        }

        /// <summary>Confirms receipt of a payload; PostMarker then stops delivering it.</summary>
        public Task AcknowledgeAsync(string sessionId, int sequenceNumber, CancellationToken ct) =>
            CallAsync("Acknowledge", ct,
                new XElement(Tns + "sessionID", sessionId),
                new XElement(Tns + "sessionSeqNo", sequenceNumber));

        /// <summary>Accepts the deal with the given sequence number (sent once it is booked in MX3).</summary>
        public Task AcceptAsync(string sessionId, int sequenceNumber, string notes, CancellationToken ct) =>
            CallAsync("Accept", ct,
                new XElement(Tns + "sessionID", sessionId),
                new XElement(Tns + "sequenceNumber", sequenceNumber),
                new XElement(Tns + "approvingUADID", NoApprovingUser),
                new XElement(Tns + "notes", notes));

        public Task DisconnectAsync(string sessionId, CancellationToken ct) =>
            CallAsync("Disconnect", ct, new XElement(Tns + "sessionID", sessionId));

        /// <summary>
        /// Posts one SOAP request and returns the {operation}Response element. A SOAP fault throws
        /// PostMarkerFaultException; any other bad response throws PostMarkerException.
        /// </summary>
        private async Task<XElement> CallAsync(string operation, CancellationToken ct, params XElement[] parameters)
        {
            var envelope = new XElement(SoapNs + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", SoapNs),
                new XElement(SoapNs + "Body",
                    new XElement(Tns + operation, parameters)));

            using var request = new HttpRequestMessage(HttpMethod.Post, _serviceUri)
            {
                Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("SOAPAction", $"\"http://tempuri.org/{operation}\"");

            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            XDocument document;
            try
            {
                document = XDocument.Parse(body);
            }
            catch (Exception ex)
            {
                throw new PostMarkerException($"{operation}: HTTP {(int)response.StatusCode}, response is not XML.", ex);
            }

            var soapBody = document.Root?.Element(SoapNs + "Body")
                ?? throw new PostMarkerException($"{operation}: HTTP {(int)response.StatusCode}, no SOAP body.");

            var fault = soapBody.Element(SoapNs + "Fault");
            if (fault != null)
                throw new PostMarkerFaultException($"{operation}: {(string?)fault.Element("faultstring") ?? "SOAP fault"}");

            if (!response.IsSuccessStatusCode)
                throw new PostMarkerException($"{operation}: HTTP {(int)response.StatusCode}.");

            return soapBody.Element(Tns + operation + "Response")
                ?? throw new PostMarkerException($"{operation}: response element missing.");
        }

        public void Dispose() => _http.Dispose();
    }
}