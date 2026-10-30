// Desktop's default encoding is HEX; registry-backed settings are not used in this isolated test.
namespace OpenCredential.Plugin.DatabaseAuth;

internal static class Settings
{
    public enum HashEncoding { HEX, BASE_64 }
    public static HashEncoding GetHashEncoding() => HashEncoding.HEX;
}
