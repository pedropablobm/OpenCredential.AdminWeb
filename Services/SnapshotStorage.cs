using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OpenCredential.AdminWeb.Services;

// Persistence has a separate serialization contract. HTTP never uses these options.
internal static class SnapshotStorage
{
    public static JsonSerializerOptions CreateOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(type =>
        {
            if (type.Type == typeof(UserAccount))
            {
                var property = type.Properties.Single(item => item.Name == "passwordHash");
                property.Get = value => ((UserAccount)value).PasswordHash;
                property.Set = (value, hash) => ((UserAccount)value).PasswordHash = (string?)hash;
                property.ShouldSerialize = (_, _) => true;
            }
            if (type.Type == typeof(AdminSnapshot))
            {
                var property = type.Properties.Single(item => item.Name == "portalPasswordResetTokens");
                property.Get = value => ((AdminSnapshot)value).PortalPasswordResetTokens;
                property.Set = (value, tokens) => ((AdminSnapshot)value).PortalPasswordResetTokens.AddRange((List<PortalPasswordResetTokenRecord>?)tokens ?? []);
                property.ShouldSerialize = (_, _) => true;
            }
        });
        return new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            TypeInfoResolver = resolver,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
    }
}
