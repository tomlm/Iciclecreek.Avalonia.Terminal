# Iciclecreek.Avalonia.Terminal

A cross-platform XTerm terminal emulator control for [Avalonia UI](https://avaloniaui.net/) applications.

![NuGet](https://img.shields.io/nuget/v/Iciclecreek.Avalonia.Terminal)
![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Build Status](https://github.com/tomlm/Iciclecreek.Avalonia.TerminalWindow/actions/workflows/BuildAndRunTests.yml/badge.svg)

## Introduction

**Iciclecreek.Avalonia.Terminal** provides Avalonia controls for embedding a fully-featured terminal emulator in your cross-platform desktop applications. Built on top of [XTerm.NET](https://github.com/tomlm/XTerm.NET) for terminal emulation and [Porta.Pty](https://github.com/tomlm/Porta.Pty) for pseudo-terminal support, it offers:

- Full XTerm-compatible terminal emulation
- Cross-platform support (Windows, Linux, macOS)
- Scrollback buffer with configurable size
- Text selection and clipboard support
- Terminal window manipulation commands (resize, move, minimize, maximize, etc.)
- Dynamic title updates from terminal escape sequences
- Customizable fonts, colors, and styling

## Installation

Install via NuGet Package Manager:

```shell
dotnet add package Iciclecreek.Avalonia.Terminal
```

Or via the Package Manager Console in Visual Studio:

```powershell
Install-Package Iciclecreek.Avalonia.Terminal
```

## Usage

### TerminalControl

`TerminalControl` is a templated control that provides a terminal view with an integrated scrollbar. Use this when you want to embed a terminal within your own window or layout.

**XAML:**

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:terminal="using:Iciclecreek.Terminal"
        x:Class="MyApp.MainWindow">
    
    <terminal:TerminalControl x:Name="Terminal"
                              FontFamily="Cascadia Mono"
                              FontSize="14"
                              BufferSize="1000"
                              ProcessExited="OnProcessExited"/>
</Window>
```

**Code-behind:**

```csharp
using Avalonia.Controls;
using Iciclecreek.Terminal;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnProcessExited(object? sender, ProcessExitedEventArgs e)
    {
        // Handle process exit (e.g., close window)
        Close();
    }
}
```

**Properties:**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Process` | `string` | `cmd.exe` (Windows) / `sh` (Unix) | The shell or process to launch |
| `Args` | `IList<string>` | Empty | Command-line arguments for the process |
| `BufferSize` | `int` | `1000` | Scrollback buffer size (number of lines) |
| `FontFamily` | `FontFamily` | Inherited | Terminal font family (use monospace fonts) |
| `FontSize` | `double` | Inherited | Terminal font size |
| `Foreground` | `IBrush` | Inherited | Default text color |
| `Background` | `IBrush` | Inherited | Terminal background color |
| `SelectionBrush` | `IBrush` | Semi-transparent blue | Text selection highlight color |

**Events:**

| Event | Description |
|-------|-------------|
| `ProcessExited` | Raised when the PTY process exits |
| `TitleChanged` | Raised when the terminal title changes (via escape sequences) |
| `BellRang` | Raised when the terminal bell is activated |
| `WindowMoved` | Raised when a window move command is received |
| `WindowResized` | Raised when a window resize command is received |
| `WindowMinimized` | Raised when a minimize command is received |
| `WindowMaximized` | Raised when a maximize command is received |
| `WindowRestored` | Raised when a restore command is received |
| `WindowFullscreened` | Raised when a fullscreen command is received |

### TerminalWindow

`TerminalWindow` is a complete window implementation that automatically handles terminal events like title changes and window manipulation commands. Use this when you want a standalone terminal window.

**XAML:**

```xml
<terminal:TerminalWindow xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:terminal="using:Iciclecreek.Terminal"
                         x:Class="MyApp.TerminalWindow"
                         Title="Terminal"
                         Width="800"
                         Height="600"
                         FontFamily="Consolas"
                         FontSize="12"
                         Background="Black"
                         Foreground="White"
                         CloseOnProcessExit="True"
                         UpdateTitleFromTerminal="True"
                         HandleWindowCommands="True"/>
```

**Or create programmatically:**

```csharp
using Iciclecreek.Terminal;

var terminalWindow = new TerminalWindow
{
    Title = "My Terminal",
    Width = 800,
    Height = 600,
    FontFamily = new FontFamily("Cascadia Mono"),
    FontSize = 14,
    Process = "pwsh.exe",  // PowerShell Core
    Args = new[] { "-NoLogo" },
    CloseOnProcessExit = true
};

terminalWindow.Show();
```

**Additional Properties (beyond TerminalControl):**

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `CloseOnProcessExit` | `bool` | `true` | Automatically close the window when the process exits |
| `UpdateTitleFromTerminal` | `bool` | `true` | Update window title from terminal escape sequences |
| `HandleWindowCommands` | `bool` | `true` | Handle window manipulation commands from the terminal |

## Links

- **GitHub Repository:** [https://github.com/tomlm/Iciclecreek.Avalonia.TerminalWindow](https://github.com/tomlm/Iciclecreek.Avalonia.TerminalWindow)
- **NuGet Package:** [https://www.nuget.org/packages/Iciclecreek.Avalonia.Terminal](https://www.nuget.org/packages/Iciclecreek.Avalonia.Terminal)
- **XTerm.NET:** [https://github.com/tomlm/XTerm.NET](https://github.com/tomlm/XTerm.NET)
- **Porta.Pty:** [https://github.com/tomlm/Porta.Pty](https://github.com/tomlm/Porta.Pty)
- **Avalonia UI:** [https://avaloniaui.net/](https://avaloniaui.net/)

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
