// Share internal types with unit tests in debug builds - we do it like this to avoid issues with signing (sharing to unsigned assemblies in release builds).
#if DEBUG
using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("FulcrumFS.Videos.Tests")]
#endif
