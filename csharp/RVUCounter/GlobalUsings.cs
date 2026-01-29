// Disambiguate WPF vs WinForms types (WinForms is included only for ColorDialog)
global using Application = System.Windows.Application;
global using Point = System.Windows.Point;
global using UserControl = System.Windows.Controls.UserControl;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using MessageBox = System.Windows.MessageBox;
global using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
global using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
global using Binding = System.Windows.Data.Binding;
global using Cursors = System.Windows.Input.Cursors;
global using FontFamily = System.Windows.Media.FontFamily;
global using ColorConverter = System.Windows.Media.ColorConverter;
