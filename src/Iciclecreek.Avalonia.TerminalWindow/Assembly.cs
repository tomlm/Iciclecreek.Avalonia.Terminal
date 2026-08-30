using Avalonia.Metadata;
using System.Runtime.CompilerServices;

// GraphemeRuns is internal — it is a run-building detail, not part of the control's surface — but its whole
// job is a text transformation that deserves direct tests rather than being probed through a rendered frame.
[assembly: InternalsVisibleTo("Iciclecreek.Avalonia.Terminal.Tests")]

// The render bench renders the same frame through both text pipelines and compares the pixels,
// which needs the switch that chooses between them.
[assembly: InternalsVisibleTo("Terminal.RenderBench")]


[assembly: XmlnsDefinition("https://github.com/tomlm/Terminal", "Iciclecreek.Terminal")]