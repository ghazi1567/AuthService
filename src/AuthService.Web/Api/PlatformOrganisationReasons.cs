namespace AuthService.Web.Api;

/// <summary>The reason codes for suspending or reactivating an organisation, in plain words. The audit log keeps the code, not the words.</summary>
public static class PlatformOrganisationReasons
{
    public static string Label(string code) => code switch
    {
        "non_payment" => "Non-payment",
        "abuse" => "Abuse of the service",
        "security" => "Security concern",
        "customer_request" => "The customer asked for it",
        "resolved" => "The problem is resolved",
        _ => "Another reason",
    };
}
