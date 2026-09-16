using Xunit;

namespace FruityLink.Hosting.Tests;

// WPF initializes process-wide DPI settings. Do not create native parent/child handles while
// another collection is starting WPF, or the two HWNDs can receive different DPI contexts.
[CollectionDefinition("Windows hosting", DisableParallelization = true)]
public sealed class WindowsHostingCollection;
