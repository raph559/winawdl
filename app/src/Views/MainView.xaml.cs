using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Winawdl.Model;
using Winawdl.Shell;
using Winawdl.Theme;

namespace Winawdl.Views;

public partial class MainView : UserControl
{
    private bool _sheetShown;

    public MainView()
    {
        InitializeComponent();
        DataContextChanged += (_, change) =>
        {
            if (change.OldValue is TableModel old) old.PropertyChanged -= OnModelChanged;
            if (change.NewValue is TableModel model) model.PropertyChanged += OnModelChanged;
        };
        DragEnter += OnDragOverTable;
        DragOver += OnDragOverTable;
        DragLeave += (_, _) => Hand.IsTarget = false;
        Drop += OnDropOnTable;
    }

    private TableModel? Model => DataContext as TableModel;

    private void OnRadioSwitch(object sender, RoutedEventArgs e)
    {
        if (Model is not { } model || sender is not Switch radio) return;
        var wanted = radio.IsChecked == true;
        // The same event comes when the model moves the switch; only a person asks for a change.
        if (wanted == model.RadioOn) return;
        model.RadioOn = wanted;
        // The radio may not follow the switch: Windows asks first, and the answer can be no.
        radio.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateTarget();
    }

    /// <summary>Something new on the table shows where it came to rest: a file
    /// drops in from above and settles, anything else slides in a few pixels.</summary>
    private void OnArrived(object sender, RoutedEventArgs e)
    {
        if (Motion.Reduced || sender is not FrameworkElement element) return;
        var move = new TranslateTransform();
        element.RenderTransform = move;
        if (element.Tag as string == "drop")
        {
            Motion.From(move, TranslateTransform.YProperty, -22, Motion.Land, Motion.Settle);
            Motion.From(element, OpacityProperty, 0, Motion.Quick);
        }
        else
        {
            Motion.From(move, TranslateTransform.YProperty, -8, Motion.Move);
            Motion.From(element, OpacityProperty, 0, Motion.Move);
        }
    }

    private static string[]? FilesIn(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) ? e.Data.GetData(DataFormats.FileDrop) as string[] : null;

    private void OnDragOverTable(object sender, DragEventArgs e)
    {
        var files = FilesIn(e) is not null;
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        Hand.IsTarget = files;
        e.Handled = true;
    }

    private void OnDropOnTable(object sender, DragEventArgs e)
    {
        Hand.IsTarget = false;
        if (FilesIn(e) is { } files) Model?.Hold(files);
        e.Handled = true;
    }

    private void OnDragOverPeer(object sender, DragEventArgs e)
    {
        // Over a device the files go to it, not into the hand.
        e.Effects = FilesIn(e) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        Hand.IsTarget = false;
        e.Handled = true;
    }

    private void OnDropOnPeer(object sender, DragEventArgs e)
    {
        if (FilesIn(e) is { } files && (sender as FrameworkElement)?.DataContext is PeerModel peer)
            Model?.SendTo(peer, files);
        e.Handled = true;
    }

    /// <summary>A received file carried out of the window, from where it was
    /// pressed. Null when nothing is being carried out.</summary>
    private string? _carried;
    private Point? _grabbedAt;

    private void OnFileGrabbed(object sender, MouseButtonEventArgs e) =>
        _grabbedAt = e.GetPosition((IInputElement)sender);

    private void OnFileCarried(object sender, MouseEventArgs e)
    {
        if (_grabbedAt is not { } start || sender is not Button button) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _grabbedAt = null;
            return;
        }
        var moved = e.GetPosition(button) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        _grabbedAt = null;
        if (button.DataContext is not FileItem file) return;
        _carried = file.Path;
        try
        {
            Drag.Out(button, file.Path);
        }
        finally
        {
            _carried = null;
            Hand.IsTarget = false;
            // The press ended outside the button's sight; without this it would stay sunk.
            button.ReleaseMouseCapture();
        }
        e.Handled = true;
    }

    /// <summary>A received file let go on the shelf it came from stays there,
    /// rather than going into the hand to be sent.</summary>
    private void OnDragOverShelf(object sender, DragEventArgs e)
    {
        if (_carried is null) return;
        e.Effects = DragDropEffects.None;
        Hand.IsTarget = false;
        e.Handled = true;
    }

    private void OnDropOnShelf(object sender, DragEventArgs e)
    {
        if (_carried is null) return;
        Hand.IsTarget = false;
        e.Handled = true;
    }

    private void OnVeilPressed(object sender, MouseButtonEventArgs e) => Model?.CloseSettings.Execute(null);

    private void OnNameLeft(object sender, KeyboardFocusChangedEventArgs e) => Model?.CommitName();

    private void OnNameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Model?.CommitName();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (Model is not { } model || model.SettingsOpen == _sheetShown) return;
        _sheetShown = model.SettingsOpen;
        if (_sheetShown)
        {
            var slide = new TranslateTransform();
            SheetPanel.RenderTransform = slide;
            Motion.From(slide, TranslateTransform.XProperty, 28, Motion.Move);
            Motion.From(Sheet, OpacityProperty, 0, Motion.Quick);
            Dispatcher.BeginInvoke(() => CloseSheet.Focus());
        }
        else
        {
            Dispatcher.BeginInvoke(() => SettingsButton.Focus());
        }
    }
}
