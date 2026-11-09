# Changelog

## Unreleased

- AB#8582 Fix: the service-account health check no longer reports every adapter account as Unhealthy because of
  `FileManagement`. The identity service grants that role by design to every `octo-pipeline-sa-*` client (AB#6467) and
  the controller's reconcile never removes it, so it is now expected: the new named list
  `PipelineServiceAccountProvisioningService.IdentityGrantedRoleNames` (next to `BaselineRoleNames`) holds the roles
  identity grants. They are tolerated, not required - an account without `FileManagement` (System.Files not installed)
  is not drift either - and only for clients with the `octo-pipeline-sa-` prefix; on a custom client id the role stays
  unexpected. Any other role that was not declared is still reported as superfluous. The rights analysis gives the same
  answer: a declared `FileManagement` is a baseline role, not superfluous, and a type that `FileManagement` grants is
  covered.

- AB#8563 Fix: pipeline service accounts hold `StreamDataReader` in addition to their declared roles, so pipelines that
  read stream data (EC allocation, billing, simulation) keep working when the asset repository enforces the stream-data
  read role (AB#8559, `OCTO_STREAMDATAREADAUTHORIZATION__ROLEENFORCEMENT=Enforce`, error `StreamDataReadRoleRequired`).
  The role is a baseline (`PipelineServiceAccountProvisioningService.BaselineRoleNames`) added when a declarative account
  (`AssignedRoleNames` set; every account created since CK 3.32.0) is reconciled; it is not written into
  `AssignedRoleNames` and does not replace declared roles. Existing accounts get it on the next system-initiated
  reconcile (tenant start or `clearCache`, workload deploy), without a manual step. The service-account health check and
  the rights analysis treat it as expected. **Operator note:** legacy accounts without `AssignedRoleNames` (provisioned
  before CK 3.32.0) are not touched - grant `StreamDataReader` to them by hand
  (`octo-cli AddClientToRole --clientId octo-pipeline-sa-<adapterRtId> --roleName StreamDataReader`) before switching
  the asset repository to `Enforce`.
