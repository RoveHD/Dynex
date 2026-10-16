using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("Dynex.Core.Tests")]
#if DEBUG
// Isolated Android emulator checks compile the same protocol regressions.
[assembly: InternalsVisibleTo("Dynex.Android")]
#endif
