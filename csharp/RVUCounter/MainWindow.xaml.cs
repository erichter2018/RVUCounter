using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using RVUCounter.Core;
using RVUCounter.Models;
using RVUCounter.ViewModels;
using RVUCounter.Views;
using Serilog;

namespace RVUCounter;

/// <summary>
/// Main window code-behind.
/// </summary>
public partial class MainWindow : Window
{
    private MainViewModel? ViewModel => DataContext as MainViewModel;
    private HwndSource? _hwndSource;

    // WM_COPYDATA constants for MosaicTools integration
    private const int WM_COPYDATA = 0x004A;
    private const int CYCLEDATA_STUDY_SIGNED = 1;           // Study signed, no critical result
    private const int CYCLEDATA_STUDY_UNSIGNED = 2;         // Study unsigned, no critical result
    private const int CYCLEDATA_STUDY_SIGNED_CRITICAL = 3;  // Study signed with critical result
    private const int CYCLEDATA_STUDY_UNSIGNED_CRITICAL = 4; // Study unsigned with critical result

    [StructLayout(LayoutKind.Sequential)]
    private struct COPYDATASTRUCT
    {
        public IntPtr dwData;
        public int cbData;
        public IntPtr lpData;
    }

    public MainWindow()
    {
        InitializeComponent();

        // Apply dark title bar based on current theme
        ThemeManager.ApplyCurrentThemeTitleBar(this);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Hook into window procedure for WM_COPYDATA messages from MosaicTools
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        Log.Debug("WM_COPYDATA hook installed for MosaicTools integration");

        // Start named pipe client for MosaicTools bridge
        ViewModel?.StartPipeClient();

        // Subscribe to scroll to top event
        if (ViewModel != null)
        {
            ViewModel.ScrollToTopRequested += OnScrollToTopRequested;
        }

        // Restore saved window position
        var savedPos = ViewModel?.GetSavedWindowPosition();
        Log.Debug("MainWindow loaded - saved position: {Pos}", savedPos != null
            ? $"X={savedPos.X}, Y={savedPos.Y}, W={savedPos.Width}, H={savedPos.Height}"
            : "null");

        if (savedPos != null && savedPos.Width > 0 && savedPos.Height > 0)
        {
            // Validate position is on screen
            var screenWidth = SystemParameters.VirtualScreenWidth;
            var screenHeight = SystemParameters.VirtualScreenHeight;
            var screenLeft = SystemParameters.VirtualScreenLeft;
            var screenTop = SystemParameters.VirtualScreenTop;

            Log.Debug("Virtual screen bounds: L={L}, T={T}, W={W}, H={H}",
                screenLeft, screenTop, screenWidth, screenHeight);

            // Check if saved position is within virtual screen bounds (handles multi-monitor)
            if (savedPos.X >= screenLeft && savedPos.X < screenLeft + screenWidth - 50 &&
                savedPos.Y >= screenTop && savedPos.Y < screenTop + screenHeight - 50)
            {
                Left = savedPos.X;
                Top = savedPos.Y;
                Width = savedPos.Width;
                Height = savedPos.Height;
                Log.Debug("Restored window position: X={X}, Y={Y}", Left, Top);
                return;
            }
            else
            {
                Log.Warning("Saved position out of screen bounds, using default");
            }
        }

        // Position at right side of primary screen if no valid saved position
        var primaryWidth = SystemParameters.PrimaryScreenWidth;
        var primaryHeight = SystemParameters.PrimaryScreenHeight;
        Width = 260;
        Height = 580;
        Left = primaryWidth - Width - 20;
        Top = (primaryHeight - Height) / 2;
        Log.Debug("Using default position: X={X}, Y={Y}", Left, Top);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Unhook WM_COPYDATA handler
        _hwndSource?.RemoveHook(WndProc);

        // Unsubscribe from scroll to top event
        if (ViewModel != null)
        {
            ViewModel.ScrollToTopRequested -= OnScrollToTopRequested;
        }

        // Save window position before disposing
        Log.Debug("MainWindow closing - saving position: X={X}, Y={Y}, W={W}, H={H}", Left, Top, Width, Height);
        ViewModel?.SaveWindowPosition(Left, Top, Width, Height);
        ViewModel?.Dispose();
    }

    /// <summary>
    /// Window procedure hook to handle WM_COPYDATA messages from MosaicTools.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_COPYDATA)
        {
            try
            {
                var cds = Marshal.PtrToStructure<COPYDATASTRUCT>(lParam);
                var messageType = cds.dwData.ToInt32();

                if (cds.cbData <= 0 || cds.lpData == IntPtr.Zero)
                    return IntPtr.Zero;

                var accession = Marshal.PtrToStringUni(cds.lpData, cds.cbData / sizeof(char));

                if (!string.IsNullOrEmpty(accession))
                {
                    bool hasCritical = (messageType == CYCLEDATA_STUDY_SIGNED_CRITICAL ||
                                        messageType == CYCLEDATA_STUDY_UNSIGNED_CRITICAL);

                    if (messageType == CYCLEDATA_STUDY_SIGNED || messageType == CYCLEDATA_STUDY_SIGNED_CRITICAL)
                    {
                        Log.Information("MosaicTools: Study SIGNED{Critical} - {Accession}",
                            hasCritical ? " (CRITICAL)" : "", accession);
                        ViewModel?.HandleMosaicToolsSignedStudy(accession, hasCritical);
                        handled = true;
                    }
                    else if (messageType == CYCLEDATA_STUDY_UNSIGNED || messageType == CYCLEDATA_STUDY_UNSIGNED_CRITICAL)
                    {
                        Log.Information("MosaicTools: Study UNSIGNED{Critical} - {Accession}",
                            hasCritical ? " (CRITICAL)" : "", accession);
                        ViewModel?.HandleMosaicToolsUnsignedStudy(accession, hasCritical);
                        handled = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error processing WM_COPYDATA message");
            }
        }

        return IntPtr.Zero;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void PaceCar_Click(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel == null) return;

        // Get click position for dialog placement
        var clickPos = PointToScreen(e.GetPosition(this));

        // Open the pace comparison dialog
        var dialog = new PaceComparisonDialog(
            ViewModel.DataManager,
            (mode, shift) =>
            {
                // Only call the command - it handles setting the mode
                try
                {
                    ViewModel.ChangePaceComparisonCommand.Execute(mode);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "Error changing pace comparison mode to {Mode}", mode);
                }
            },
            new Point(clickPos.X, clickPos.Y + 10));

        dialog.Owner = this;
        dialog.Show();
    }

    private void OnScrollToTopRequested(object? sender, EventArgs e)
    {
        // Scroll the recent studies list to top
        RecentStudiesScrollViewer?.ScrollToTop();
    }

    private void TeamPanelHeader_Click(object sender, MouseButtonEventArgs e)
    {
        // Toggle team panel collapse/expand
        ViewModel?.ToggleTeamPanelCommand.Execute(null);
    }

    private void TeamModeToggle_Click(object sender, MouseButtonEventArgs e)
    {
        // Toggle between Rate and Total view modes
        ViewModel?.ToggleTeamViewModeCommand.Execute(null);
        e.Handled = true; // Prevent header click from also firing
    }
}