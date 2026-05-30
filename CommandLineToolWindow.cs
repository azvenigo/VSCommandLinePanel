using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using System;
using System.Runtime.InteropServices;

namespace VS_LaunchArguments
{
    /// <summary>
    /// This class implements the tool window exposed by this package and hosts a user control.
    /// </summary>
    /// <remarks>
    /// In Visual Studio tool windows are composed of a frame (implemented by the shell) and a pane,
    /// usually implemented by the package implementer.
    /// <para>
    /// This class derives from the ToolWindowPane class provided from the MPF in order to use its
    /// implementation of the IVsUIElementPane interface.
    /// </para>
    /// </remarks>
    [Guid("2adebb05-27f8-4127-a198-dea0b043889e")]
    public class CommandLineToolWindow : ToolWindowPane
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommandLineToolWindow"/> class.
        /// </summary>
        public CommandLineToolWindow() : base(null)
        {
            this.Caption = "Command Args Panel";
            this.BitmapImageMoniker = KnownMonikers.Console;
            this.Content = new CommandLineToolWindowControl();
        }
    }
}
