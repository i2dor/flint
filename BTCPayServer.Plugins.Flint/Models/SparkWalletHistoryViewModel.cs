using System.Collections.Generic;
using BTCPayServer.Plugins.Flint.Sdk;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace BTCPayServer.Plugins.Flint.Models;

public class SparkWalletHistoryViewModel
{
    [BindNever]
    public string StoreId { get; set; } = "";

    /// <summary>Sent and received payments, newest first.</summary>
    public IReadOnlyList<SparkPayment> Payments { get; set; } = [];
}
