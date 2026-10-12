using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Serialises a configuration entity for an adapter with its <c>Secret</c> attributes revealed
/// (AB#5537, concept §5.2 phase 2 — <c>SerializeWithRevealedSecrets</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why reveal at all.</b> Pipelines read their credentials from the configuration list the
/// controller ships with every pipeline (<c>ConfigurationDto.ConfigurationValue</c> → the adapter's
/// <c>GlobalConfiguration</c>). The engine serialises an <c>RtSecretValue</c> only as the marker
/// <c>{"isSet":true}</c>, so without this step every adapter would lose its passwords the moment
/// System.Communication 4.8 turned them into <c>Secret</c>. The plaintext travels exactly where it
/// travelled before — over the adapter's authenticated SignalR connection — and is otherwise held
/// only in the controller's in-memory adapter cache (never published; see
/// <c>AdapterCache.PublishConfigurationAsync</c>).
/// </para>
/// <para>
/// <b>Shape.</b> The output is the same camelCase JSON <c>Serialize()</c> writes; only the values
/// of Secret attributes change from the marker to the plaintext string — in the <c>attributes</c>
/// dictionary (PascalCase keys) and in a typed entity's generated property (camelCase), at any
/// record (array) depth. An entity without Secret values is serialised by <c>Serialize()</c>
/// itself, byte for byte as before.
/// </para>
/// <para>
/// 🔴 <b>Never logged, never pending, never fatal.</b> A <c>Pending</c> value is not revealed
/// (it becomes <c>null</c>); a value that cannot be decrypted (key ring missing, unknown key id,
/// tampered) becomes <c>null</c> with an error naming tenant, entity and attribute — never the
/// value — so one broken credential cannot take every other pipeline of the adapter down with it.
/// </para>
/// </remarks>
internal static class ConfigurationSecretSerializer
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Same options as Meshmakers.Common.Shared.ObjectExtensions.Serialize — the adapter
    // deserialises with them.
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private const string AttributesPropertyName = "attributes";

    /// <summary>
    /// Serialises <paramref name="entity" /> for an adapter, revealing every Secret value.
    /// </summary>
    /// <param name="entity">The configuration entity</param>
    /// <param name="encryptionService">The controller's reveal point</param>
    /// <param name="tenantId">Tenant of the entity (decrypt counter tag, log context)</param>
    /// <returns>The JSON the adapter receives</returns>
    public static string SerializeWithRevealedSecrets(this RtEntity entity,
        IWorkloadEncryptionService encryptionService, string tenantId)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(encryptionService);

        if (!ContainsSecret(entity))
        {
            return entity.Serialize();
        }

        var node = JsonSerializer.SerializeToNode(entity, entity.GetType(), SerializerOptions)
                   ?? throw new InvalidOperationException("Serialising a configuration entity produced no JSON.");
        var context = new RevealContext(encryptionService, tenantId, entity.CkTypeId?.ToString(),
            entity.RtId.ToString());
        RevealAttributes(entity, node as JsonObject, context, null);
        return node.ToJsonString(SerializerOptions);
    }

    private static bool ContainsSecret(object? value)
    {
        return value switch
        {
            RtSecretValue => true,
            RtTypeWithAttributes withAttributes => withAttributes.Attributes.Values.Any(ContainsSecret),
            string => false,
            IEnumerable enumerable => enumerable.Cast<object?>().Any(ContainsSecret),
            _ => false
        };
    }

    private static void RevealAttributes(RtTypeWithAttributes owner, JsonObject? ownerNode, RevealContext context,
        string? path)
    {
        if (ownerNode == null)
        {
            return;
        }

        var attributesNode = ownerNode[AttributesPropertyName] as JsonObject;
        foreach (var (name, value) in owner.Attributes)
        {
            if (!ContainsSecret(value))
            {
                continue;
            }

            var attributePath = path == null ? name : $"{path}.{name}";
            if (value is RtSecretValue secret)
            {
                var plaintext = RevealOrNull(secret, name, attributePath, context);
                if (attributesNode != null && attributesNode.ContainsKey(name))
                {
                    attributesNode[name] = plaintext;
                }

                // A typed entity / record also serialises the generated property.
                var propertyName = JsonNamingPolicy.CamelCase.ConvertName(name);
                if (ownerNode.ContainsKey(propertyName))
                {
                    ownerNode[propertyName] = plaintext;
                }

                continue;
            }

            RevealNested(value, attributesNode?[name], context, attributePath);
            RevealNested(value, ownerNode[JsonNamingPolicy.CamelCase.ConvertName(name)], context, attributePath);
        }
    }

    private static void RevealNested(object? value, JsonNode? node, RevealContext context, string path)
    {
        switch (value)
        {
            case RtTypeWithAttributes record:
                RevealAttributes(record, node as JsonObject, context, path);
                break;
            case IEnumerable enumerable when value is not string && node is JsonArray array:
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    if (index >= array.Count)
                    {
                        break;
                    }

                    if (item is RtTypeWithAttributes itemRecord)
                    {
                        RevealAttributes(itemRecord, array[index] as JsonObject, context, $"{path}[{index}]");
                    }

                    index++;
                }

                break;
            }
        }
    }

    private static string? RevealOrNull(RtSecretValue secret, string attributeName, string attributePath,
        RevealContext context)
    {
        try
        {
            // Decisions 2026-10-06, item 2: an unknown key id (e.g. after a restore from another
            // environment), a tampered or a corrupt value ships as null - RevealOrNull maps it and the
            // engine logs the error (key id, never the value).
            return context.EncryptionService.RevealOrNull(secret,
                new SecretAccessContext(context.TenantId, context.CkTypeId, attributeName));
        }
        catch (Exception e)
        {
            // Configuration problems (no key ring on this pod, strict mode rejecting legacy clear
            // text) still throw: the adapter receives the value as not set as well. Type and message
            // only: the exception types of the decrypt path never carry the value.
            Logger.Error(
                "[{TenantId}] Secret attribute '{AttributePath}' of configuration '{RtId}' ({CkTypeId}) could not be revealed ({ExceptionType}: {Reason}); the adapter receives it as not set",
                context.TenantId, attributePath, context.RtId, context.CkTypeId, e.GetType().Name, e.Message);
            return null;
        }
    }

    private sealed record RevealContext(
        IWorkloadEncryptionService EncryptionService,
        string TenantId,
        string? CkTypeId,
        string RtId);
}
