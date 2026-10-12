namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// AB#5537: response of <c>POST {tenantId}/v1/serviceAccount/switchToImpersonation</c>. Never
/// contains a secret.
/// </summary>
/// <param name="TenantId">Tenant.</param>
/// <param name="Applied"><c>false</c> = dry run, nothing was written.</param>
/// <param name="Accounts">One entry per <c>ServiceAccountConfiguration</c> of the tenant.</param>
/// <param name="RedeployedDataFlowRtIds">Data flows redeployed after the switch.</param>
/// <param name="FailedDataFlowRedeploys">Data flows whose redeploy failed — redeploy them by hand.</param>
public sealed record ServiceAccountImpersonationSwitchReportDto(
    string TenantId,
    bool Applied,
    IReadOnlyList<ServiceAccountImpersonationSwitchEntryDto> Accounts,
    IReadOnlyList<string> RedeployedDataFlowRtIds,
    IReadOnlyList<DataFlowRedeployFailureDto> FailedDataFlowRedeploys);

/// <summary>
/// AB#5537: one account of the impersonation switch report.
/// </summary>
/// <param name="ConfigurationRtId">The account's rtId.</param>
/// <param name="WellKnownName">Its well-known name.</param>
/// <param name="ClientId">Its identity client id.</param>
/// <param name="Outcome">
/// <c>WouldSwitch</c>, <c>Switched</c>, <c>SwitchedPendingInvalidation</c>, <c>AlreadyImpersonating</c>,
/// <c>SkippedAdapterOwnIdentity</c>, <c>SkippedNoCapableActor</c>, <c>SkippedIncomplete</c> or <c>Failed</c>.
/// </param>
/// <param name="Message">Reason in plain words.</param>
/// <param name="PipelineRtIds">Pipelines using the account.</param>
public sealed record ServiceAccountImpersonationSwitchEntryDto(
    string ConfigurationRtId,
    string WellKnownName,
    string? ClientId,
    string Outcome,
    string Message,
    IReadOnlyList<string> PipelineRtIds);

/// <summary>
/// AB#5537: a data flow whose redeploy after the impersonation switch failed.
/// </summary>
/// <param name="DataFlowRtId">The data flow.</param>
/// <param name="Reason">Exception type and message.</param>
public sealed record DataFlowRedeployFailureDto(string DataFlowRtId, string Reason);
