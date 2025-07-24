namespace Microsoft.CodeAnalysis;

[AttributeUsage(AttributeTargets.Struct | AttributeTargets.GenericParameter)]
internal sealed class NonCopyableAttribute : Attribute
{
}
