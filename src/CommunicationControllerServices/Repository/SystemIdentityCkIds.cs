using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// The <c>System.Identity</c> CK element ids the AB#5113 rights analysis reads (data policies,
/// data permissions and their edges).
///
/// <para>
/// Hand-maintained on purpose: the controller does not reference a generated
/// <c>System.Identity</c> CK model package (none is published — the model lives inside
/// <c>octo-identity-services</c>), and pulling one in just for four read-only ids would couple the
/// controller's build to the identity release train. These ids are the stable, unversioned runtime
/// ids as stored in every tenant's RT collections — the same string contract the identity service
/// itself persists (see <c>ConstructionKit/associations/identity-associations.yaml</c> and
/// <c>ck-dataPolicy.yaml</c> / <c>ck-dataPermission.yaml</c> in <c>octo-identity-services</c>).
/// </para>
/// </summary>
internal static class SystemIdentityCkIds
{
    /// <summary>The <c>System.Identity/DataPolicy</c> entity type (Epic AB#4969: binds a permission to CK types with scope and enforcement mode).</summary>
    public static readonly RtCkId<CkTypeId> RtCkDataPolicyTypeId = new("System.Identity/DataPolicy");

    /// <summary>The <c>System.Identity/DataPermission</c> entity type (a named data permission, granted to roles).</summary>
    public static readonly RtCkId<CkTypeId> RtCkDataPermissionTypeId = new("System.Identity/DataPermission");

    /// <summary>The <c>System.Identity/Role</c> entity type.</summary>
    public static readonly RtCkId<CkTypeId> RtCkRoleTypeId = new("System.Identity/Role");

    /// <summary>The DataPolicy → DataPermission edge (origin: policy).</summary>
    public static readonly RtCkId<CkAssociationRoleId> RtCkPolicyPermissionRoleId =
        new("System.Identity/PolicyPermission");

    /// <summary>The Role → DataPermission grant edge (origin: role).</summary>
    public static readonly RtCkId<CkAssociationRoleId> RtCkGrantsPermissionRoleId =
        new("System.Identity/GrantsPermission");
}
