using System.Runtime.InteropServices;

namespace FulcrumFS.Utilities;

internal static class StructDisposableListExtensions
{
    /// <summary>
    /// Disposes every element of a list of mutable value-type disposables in reverse order, calling <see cref="IDisposable.Dispose"/> on the original list
    /// elements via a <c>ref</c> rather than on a copy. This avoids the subtle "dispose on a copy of the struct" bug, where disposing a by-value copy leaves
    /// the stored element (and the resource it tracks, such as a held lock) un-released, which is very difficult to trace. Always use this to dispose lists of
    /// struct locks rather than indexing (<c>list[i].Dispose()</c>) or <c>foreach</c>, both of which operate on copies.
    /// </summary>
    public static void ReverseDisposeAll<T>(this List<T> list) where T : struct, IDisposable
    {
        var span = CollectionsMarshal.AsSpan(list);

        for (int i = span.Length - 1; i >= 0; i--)
        {
            ref var item = ref span[i];
            item.Dispose();
        }
    }
}
