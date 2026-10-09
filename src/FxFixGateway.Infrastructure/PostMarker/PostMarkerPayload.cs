namespace FxFixGateway.Infrastructure.PostMarker
{
    /// <summary>
    /// One payload from GetNext (PMXMLPayload). Status is RECEIVED for a new deal and ACCEPTED for
    /// PostMarker's echo of our own Accept.
    /// </summary>
    public sealed record PostMarkerPayload(int PayloadId, int SequenceNumber, string Status, int StatusVersion, string Xml);
}