using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PersonalVpn;

internal static class AppIcons
{
    private sealed record Cached(BitmapSource? Image, DateTime Read, long Used);
    private static readonly object gate=new();
    private static readonly Dictionary<string,Cached> cache=new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string,Task<BitmapSource?>> pending=new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim workers=new(2);
    private static long clock;
    public static Task<BitmapSource?> Load(string path)
    {
        lock(gate)
        {
            if(cache.TryGetValue(path,out var found) && DateTime.UtcNow-found.Read<TimeSpan.FromMinutes(5)) { cache[path]=found with { Used=++clock };return Task.FromResult(found.Image); }
            if(pending.TryGetValue(path,out var task))return task;
            var loading=Read(path);pending[path]=loading;return loading;
        }
    }
    private static async Task<BitmapSource?> Read(string path)
    {
        await workers.WaitAsync().ConfigureAwait(false);
        BitmapSource? bitmap=null;
        try
        {
            bitmap=await Task.Run(()=>
            {
                // Local resource extraction only: no process launch, UNC lookup or favicon request.
                if(path.StartsWith(@"\\",StringComparison.Ordinal) || !File.Exists(path))return null;
                using var icon=System.Drawing.Icon.ExtractAssociatedIcon(path);
                if(icon==null)return null;
                var result=Imaging.CreateBitmapSourceFromHIcon(icon.Handle,Int32Rect.Empty,BitmapSizeOptions.FromWidthAndHeight(32,32));result.Freeze();return result;
            }).ConfigureAwait(false);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException or System.Security.SecurityException) { }
        finally { workers.Release(); }
        lock(gate)
        {
            pending.Remove(path);cache[path]=new(bitmap,DateTime.UtcNow,++clock);
            while(cache.Count>96)cache.Remove(cache.MinBy(x=>x.Value.Used).Key);
        }
        return bitmap;
    }
}

internal sealed class RuleRowConverter : IValueConverter
{
    private readonly Func<TunnelException,UIElement> build;
    public RuleRowConverter(Func<TunnelException,UIElement> build) { this.build=build; }
    public object Convert(object value,Type targetType,object parameter,CultureInfo culture)=>build((TunnelException)value);
    public object ConvertBack(object value,Type targetType,object parameter,CultureInfo culture)=>throw new NotSupportedException();
}

public sealed partial class MainWindow
{
    private static FrameworkElement TargetIcon(string kind)
    {
        var paths=new GeometryGroup();
        if(kind=="Domain")
        {
            paths.Children.Add(new EllipseGeometry(new Point(12,12),10,10));paths.Children.Add(new EllipseGeometry(new Point(12,12),4,10));
            paths.Children.Add(Geometry.Parse("M2,12 L22,12"));
        }
        else if(kind=="IP")
        {
            paths.Children.Add(Geometry.Parse("M12,6 L12,11 M4,16 L4,11 L20,11 L20,16"));
            foreach(var point in new[]{new Point(12,3),new Point(4,19),new Point(20,19)})paths.Children.Add(new EllipseGeometry(point,2.5,2.5));
        }
        else foreach(double x in new[]{2d,14})foreach(double y in new[]{2d,14})paths.Children.Add(new RectangleGeometry(new Rect(x,y,7,7),1,1));
        paths.Freeze();
        return new System.Windows.Shapes.Path { Data=paths,Stroke=Color("#91BEF4"),StrokeThickness=1.5,Width=24,Height=24,Stretch=Stretch.Uniform,VerticalAlignment=VerticalAlignment.Center,ToolTip=kind=="IP"?"IP / network group":kind=="Domain"?"Domain group":"App executable" };
    }
    private static FrameworkElement ExecutableIcon(string path)
    {
        var box=new Grid { Width=32,Height=32,Margin=new Thickness(0,0,8,0) };var fallback=TargetIcon("App");box.Children.Add(fallback);
        var image=new Image { Width=28,Height=28,Stretch=Stretch.Uniform };box.Children.Add(image);
        image.Loaded+=async(_,_)=> { var source=await AppIcons.Load(path);if(image.IsLoaded && source!=null){image.Source=source;fallback.Visibility=Visibility.Collapsed;} };
        return box;
    }
    private static UIElement EditDeleteIcon(bool edit)=>new System.Windows.Shapes.Path
    {
        Data=Geometry.Parse(edit?"M3,16 L3,21 L8,21 L21,8 L16,3 Z M13,6 L18,11":"M5,6 L19,6 L18,22 L6,22 Z M3,6 L21,6 M9,3 L15,3 M10,10 L10,18 M14,10 L14,18"),
        Stroke=Color("#B5C9E3"),StrokeThickness=1.6,Width=15,Height=17,Stretch=Stretch.Uniform
    };
    private UIElement ExceptionRow(TunnelPolicy policy,bool apps,TunnelException entry)
    {
        var row=new Grid { MinHeight=38 };
        foreach(var width in new[]{new GridLength(40),new GridLength(1,GridUnitType.Star),GridLength.Auto,new GridLength(32),new GridLength(32)})row.ColumnDefinitions.Add(new ColumnDefinition{Width=width});
        var icon=apps?ExecutableIcon(entry.Value):TargetIcon(entry.Kind);Grid.SetColumn(icon,0);row.Children.Add(icon);
        var label=Text(apps?Path.GetFileName(entry.Value):entry.DisplayName,13);label.Name="RuleSummary";label.Margin=new Thickness(0,0,6,0);label.VerticalAlignment=VerticalAlignment.Center;label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;label.ToolTip=entry.Value;Grid.SetColumn(label,1);row.Children.Add(label);
        var mode=Text(entry.Action,11,entry.Action=="Block"?"#F29C9F":entry.Action=="Bypass"?"#8DB8F2":"#58DCB5");mode.Margin=new Thickness(7,4,7,4);
        var badge=new Border{Child=mode,Background=Color("#263B52"),CornerRadius=new CornerRadius(7),Margin=new Thickness(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(badge,2);row.Children.Add(badge);
        var edit=Action("",()=>EditException(policy,apps,entry));edit.Content=EditDeleteIcon(true);edit.ToolTip="Edit exception";edit.Padding=new Thickness(5);edit.Margin=new Thickness(1);edit.Height=32;Grid.SetColumn(edit,3);row.Children.Add(edit);
        var remove=Action("",()=>{policy.Entries.Remove(entry);Changed(!apps||settings.CaptureMode=="TUN");Navigate(apps?"AppTunnel":"SiteTunnel");});remove.Content=EditDeleteIcon(false);remove.ToolTip="Delete exception";remove.Padding=new Thickness(5);remove.Margin=new Thickness(1);remove.Height=32;Grid.SetColumn(remove,4);row.Children.Add(remove);
        System.Windows.Automation.AutomationProperties.SetName(edit,"Edit "+label.Text);System.Windows.Automation.AutomationProperties.SetName(remove,"Delete "+label.Text);
        var card=Card(row);card.Padding=new Thickness(8,6,8,6);card.Margin=new Thickness(0,0,0,7);return card;
    }
    private ListBox ExceptionList(TunnelPolicy policy,bool apps)
    {
        var list=new ListBox { ItemsSource=policy.Entries.ToArray(),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Padding=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Stretch };
        ScrollViewer.SetCanContentScroll(list,true);ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);ScrollViewer.SetVerticalScrollBarVisibility(list,ScrollBarVisibility.Auto);
        VirtualizingStackPanel.SetIsVirtualizing(list,true);VirtualizingStackPanel.SetVirtualizationMode(list,VirtualizationMode.Recycling);
        var item=new FrameworkElementFactory(typeof(ContentControl));item.SetValue(ContentControl.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch);item.SetBinding(ContentControl.ContentProperty,new Binding{Converter=new RuleRowConverter(entry=>ExceptionRow(policy,apps,entry))});
        list.ItemTemplate=new DataTemplate{VisualTree=item};
        var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(0)));style.Setters.Add(new Setter(Control.IsTabStopProperty,false));
        style.Setters.Add(new Setter(Control.TemplateProperty,(ControlTemplate)System.Windows.Markup.XamlReader.Parse("<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"ListBoxItem\"><ContentPresenter HorizontalAlignment=\"Stretch\"/></ControlTemplate>")));
        list.ItemContainerStyle=style;return list;
    }
}
