namespace FxFixGateway.Infrastructure.PostMarker
{
    /// <summary>A PostMarker call failed: unexpected HTTP response or a response that isn't valid SOAP.</summary>
    public class PostMarkerException : Exception
    {
        public PostMarkerException(string message) : base(message) { }

        public PostMarkerException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>PostMarker answered with a SOAP fault — the service itself refused the call.</summary>
    public sealed class PostMarkerFaultException : PostMarkerException
    {
        public PostMarkerFaultException(string message) : base(message) { }
    }
}