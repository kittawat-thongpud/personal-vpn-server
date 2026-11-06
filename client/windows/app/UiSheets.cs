using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PersonalVpn;

public sealed partial class MainWindow
{
    private Grid? sheet;
    private IInputElement? sheetPreviousFocus;
    private void CloseSheet()
    {
        if (sheet == null) return;
        layoutRoot.Children.Remove(sheet); sheet = null;
        if (sheetPreviousFocus != null) Keyboard.Focus(sheetPreviousFocus);
        sheetPreviousFocus = null;
    }
    private void ShowSheet(string title, UIElement body)
    {
        CloseSheet(); sheetPreviousFocus = Keyboard.FocusedElement;
        var overlay = new Grid { Background = Color("#99050C17") };
        var panel = new StackPanel { Margin = new Thickness(20, 14, 20, 18) };
        var heading = new DockPanel(); var close = Action("×", CloseSheet); close.MinWidth = 38; close.ToolTip = "Close"; DockPanel.SetDock(close,Dock.Right); heading.Children.Add(close);
        heading.Children.Add(Text(title, 21)); panel.Children.Add(heading); panel.Children.Add(body);
        var surface = new Border { Background = Color("#192A40"), BorderBrush = Color("#405670"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(22,22,0,0), MaxHeight = Math.Max(200,ActualHeight - 90), VerticalAlignment=VerticalAlignment.Bottom, Child=WidgetScroll(panel) };
        overlay.Children.Add(surface);
        overlay.MouseDown += (_, e) => { if (ReferenceEquals(e.OriginalSource, overlay)) { e.Handled=true; CloseSheet(); } };
        overlay.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled=true; CloseSheet(); } };
        KeyboardNavigation.SetTabNavigation(overlay,KeyboardNavigationMode.Cycle);
        sheet=overlay; layoutRoot.Children.Add(overlay); close.Focus();
    }
    private FrameworkElement LatencyLabel(ServerProfile profile)
    {
        var target=LatencyTarget.From(profile,settings.Protocol); var measured=latency.Get(target);
        var text=Text(measured.Label,11,"#91BEF4"); text.VerticalAlignment=VerticalAlignment.Center; text.ToolTip=measured.Detail;
        latencyLabels.Add((text,target));
        var frame=new Border { Child=text, MinWidth=55, MinHeight=38, Padding=new Thickness(5,0,5,0), Background=Brushes.Transparent, Focusable=true, Cursor=Cursors.Hand };
        AutomationProperties.SetName(frame,"Server latency for " + profile.Name);
        void Open()
        {
            var panel=Stack(); panel.Children.Add(Text(latency.Get(target).Detail,12,"#A3B4CD"));
            panel.Children.Add(Action("Refresh latency",()=> { if(target!=null)latency.Refresh(only:target);CloseSheet(); })); ShowSheet("Server latency",panel);
        }
        frame.MouseLeftButtonDown+=(_,e)=>{e.Handled=true;Open();};
        frame.KeyDown+=(_,e)=>{if(e.Key is Key.Enter or Key.Space){e.Handled=true;Open();}};
        return frame;
    }
}
