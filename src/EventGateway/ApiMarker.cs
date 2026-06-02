namespace EventGateway;

/// <summary>
/// Marker type used by integration tests to locate this service's entry-point
/// assembly via WebApplicationFactory. Both services expose a top-level
/// Program, so a distinct marker per service avoids a type-name clash when one
/// test project references both.
/// </summary>
public sealed class ApiMarker;
