using System;
using ZecaTV.UI;

namespace ZecaTV
{
    /// <summary>Read-only reference for every keyboard/mouse shortcut, moved out of the main window's
    /// now-playing panel (which shows EPG info instead) and into its own window, opened from Settings.</summary>
    public partial class ControlsWindow : System.Windows.Window
    {
        public ControlsWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            DarkTitleBar.Apply(this);
        }
    }
}
