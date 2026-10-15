namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Options;

/// <summary>
///     Guard of the impersonation switch (AB#5537 / AB#5114, concept decision 7): the maintenance
///     operation that clears the stored client secret of standalone pipeline service accounts.
/// </summary>
/// <remarks>
///     Clearing a credential cannot be undone (the old secret is invalidated identity-side), so the
///     operation only writes when an operator armed it explicitly per environment —
///     <c>OCTO_SERVICEACCOUNTIMPERSONATION__ALLOWSECRETCLEARING=true</c> — after reading the dry-run
///     report. The dry run (classification only) is always available.
/// </remarks>
internal class ServiceAccountImpersonationOptions
{
    /// <summary>
    ///     Configuration section.
    /// </summary>
    public const string SectionName = "ServiceAccountImpersonation";

    /// <summary>
    ///     Whether <c>POST {tenantId}/v1/serviceAccount/switchToImpersonation?apply=true</c> may clear
    ///     secrets. Default <c>false</c>: the request is refused with 409.
    /// </summary>
    public bool AllowSecretClearing { get; set; }
}
