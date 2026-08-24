using Avalonia.Metadata;
using System.Runtime.CompilerServices;

// GraphemeRuns is internal — it is a run-building detail, not part of the control's surface — but its whole
// job is a text transformation that deserves direct tests rather than being probed through a rendered frame.
[assembly: InternalsVisibleTo("Iciclecreek.Avalonia.Terminal.Tests")]


[assembly: XmlnsDefinition("https://github.com/tomlm/Terminal", "Iciclecreek.Terminal")]