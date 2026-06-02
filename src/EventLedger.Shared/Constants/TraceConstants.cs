namespace EventLedger.Shared.Constants;

/// <summary>
/// Constant names used for distributed tracing and service identity.
/// Centralised so the Gateway and Account Service cannot drift apart.
/// </summary>
public static class TraceConstants
{
    /// <summary>
    /// W3C Trace Context header. OpenTelemetry's HTTP instrumentation reads and
    /// writes this automatically; the constant is kept for explicit references,
    /// tests, and documentation.
    /// </summary>
    public const string TraceParentHeader = "traceparent";

    /// <summary>
    /// Property name under which the trace id is written into structured logs.
    /// </summary>
    public const string TraceIdLogProperty = "TraceId";

    /// <summary>
    /// Property name under which the span id is written into structured logs.
    /// </summary>
    public const string SpanIdLogProperty = "SpanId";

    /// <summary>
    /// Property name under which the service name is written into structured logs.
    /// </summary>
    public const string ServiceNameLogProperty = "ServiceName";
}
