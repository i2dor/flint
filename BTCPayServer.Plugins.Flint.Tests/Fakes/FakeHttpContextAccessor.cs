using Microsoft.AspNetCore.Http;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// A settable <see cref="IHttpContextAccessor"/> for tests that need to stand in for an authorised request.
/// </summary>
public sealed class FakeHttpContextAccessor : IHttpContextAccessor
{
    public HttpContext? HttpContext { get; set; }
}
