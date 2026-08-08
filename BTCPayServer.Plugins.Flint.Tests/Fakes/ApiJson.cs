using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace BTCPayServer.Plugins.Flint.Tests.Fakes;

/// <summary>
/// Serialises an API model the way BTCPay actually serialises a Greenfield response.
/// </summary>
/// <remarks>
/// <para>
/// Two things depend on getting this right, and neither is obvious by inspection. The member names on the wire are
/// <em>camelCase</em> — ASP.NET Core's <c>AddNewtonsoftJson</c> defaults carry a <c>CamelCaseNamingStrategy</c>, and
/// BTCPay does not override them — so the OpenAPI fragment has to document <c>balanceSats</c> and a validation
/// error's <c>path</c> has to say <c>maxFeePercent</c>. And enums serialise as <em>integers</em> unless the property
/// is annotated, which is why every enum on the plugin's API models carries a <c>StringEnumConverter</c> exactly as
/// core's own Greenfield models do.
/// </para>
/// <para>
/// Built from <see cref="MvcNewtonsoftJsonOptions"/>'s own constructor rather than hand-assembled, so it is the
/// framework's defaults by construction. <c>SparkPluginStartupTests</c> checks the assumption that BTCPay leaves
/// them alone, against BTCPay's real container.
/// </para>
/// </remarks>
public static class ApiJson
{
    public static JsonSerializerSettings Settings { get; } = new MvcNewtonsoftJsonOptions().SerializerSettings;

    public static string Serialize(object value) => JsonConvert.SerializeObject(value, Settings);
}
