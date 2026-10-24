#if !NET7_0_OR_GREATER
// Compiler and library members that .NET 6 (the Windows 7 build) does not have yet.
namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property, Inherited = false)]
    sealed class RequiredMemberAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
    sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
    {
        public string FeatureName { get; } = featureName;
        public bool IsOptional { get; init; }
    }
}

namespace Mandarin
{
    static class Net6Polyfills
    {
        /// <summary>StreamReader.ReadToEndAsync(CancellationToken) appeared in .NET 7.</summary>
        public static Task<string> ReadToEndAsync(this System.IO.StreamReader reader, System.Threading.CancellationToken _) => reader.ReadToEndAsync();
    }
}

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Constructor, Inherited = false)]
    sealed class SetsRequiredMembersAttribute : Attribute { }
}
#endif
