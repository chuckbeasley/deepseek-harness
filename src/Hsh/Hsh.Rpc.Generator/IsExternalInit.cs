// Polyfill: records need IsExternalInit, which netstandard2.0 does not define.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
